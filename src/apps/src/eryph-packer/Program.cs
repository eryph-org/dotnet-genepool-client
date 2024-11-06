using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.CommandLine.Rendering;
using System.Text.Json;
using Eryph.ConfigModel;
using Eryph.ConfigModel.Catlets;
using Eryph.ConfigModel.FodderGenes;
using Eryph.ConfigModel.Json;
using Eryph.ConfigModel.Yaml;
using Eryph.GenePool.Client;
using Eryph.GenePool.Client.Requests;
using Eryph.GenePool.Model;
using Eryph.GenePool.Packing;
using Eryph.Packer;
using LanguageExt;
using LanguageExt.Common;
using Spectre.Console;
using Spectre.Console.Json;
using Spectre.Console.Rendering;
using Command = System.CommandLine.Command;
using Validations = Eryph.GenePool.Model.Validations;

// environment handling for testing and development - packer can either use same variables as eryph or its own
// in both cases setting staging authority will use staging genepool api, overwriting the api uri
// is only required for local development
var genePoolUriString = "https://genepool-api.eryph.io";

var stagingAuthority = Environment.GetEnvironmentVariable("ERYPH_PACKER_AUTHORITY") == "staging" ||
                       Environment.GetEnvironmentVariable("ERYPH_GENEPOOL_AUTHORITY") == "staging";

if (stagingAuthority)
{
    genePoolUriString = "https://eryphgenepoolapistaging.azurewebsites.net/";

    var overwriteGenepoolApi = Environment.GetEnvironmentVariable("ERYPH_PACKER_GENEPOOL_API") 
                               ?? Environment.GetEnvironmentVariable("ERYPH_GENEPOOL_API");
    if (!string.IsNullOrWhiteSpace(overwriteGenepoolApi))
        genePoolUriString = overwriteGenepoolApi;

}

var genePoolUri = new Uri(genePoolUriString);


var clientOptions = new GenePoolClientOptions(GenePoolClientOptions.ServiceVersion.V1,
    genePoolUri.ToString(), 
    stagingAuthority);


var organizationArgument = new Argument<string>("organization", "name of organization.");
organizationArgument.AddValidation(OrganizationName.NewValidation);
var genesetArgument = new Argument<string>("geneset", "name of geneset in format organization/id/[tag]");
genesetArgument.AddValidation(GeneSetIdentifier.NewValidation);

var refArgument = new Argument<string>("referenced geneset", "name of referenced geneset in format organization/id/tag");
refArgument.AddValidation(GeneSetIdentifier.NewValidation);

var vmExportArgument = new Argument<DirectoryInfo>("vm export", "path to exported VM");
var filePathArgument = new Argument<FileInfo>("file", "path to file");

var isPublicOption = new System.CommandLine.Option<bool>("--public", "sets genesets visibility to public");
var shortDescriptionOption = new System.CommandLine.Option<string>("--description", "sets genesets description");
shortDescriptionOption.AddValidation(Validations.ValidateGenesetShortDescription);

vmExportArgument.ExistingOnly();

var apiKeyOption =
    new System.CommandLine.Option<string>("--api-key", "API key for authentication");

var workDirOption =
    // ReSharper disable once StringLiteralTypo
    new System.CommandLine.Option<DirectoryInfo>("--workdir", "work directory")
        .ExistingOnly();
    
workDirOption.SetDefaultValue(new DirectoryInfo(Environment.CurrentDirectory));


var rootCommand = new RootCommand();
rootCommand.AddGlobalOption(workDirOption);

var genesetCommand = new Command("geneset", "This command operates on a geneset.");
rootCommand.Add(genesetCommand);

var infoGenesetCommand = new Command("info", "This command reads the metadata of a geneset.");
infoGenesetCommand.AddArgument(genesetArgument);

genesetCommand.Add(infoGenesetCommand);

var initGenesetCommand =
    new Command("init", "This command initializes the filesystem structure for a geneset.");
initGenesetCommand.AddArgument(genesetArgument);
initGenesetCommand.AddOption(isPublicOption);
initGenesetCommand.AddOption(shortDescriptionOption);

genesetCommand.Add(initGenesetCommand);


var genesetTagCommand = new Command("geneset-tag", "This command operates on a geneset tag.");
rootCommand.Add(genesetTagCommand);

var infoGenesetTagCommand = new Command("info", "This command reads the metadata of a geneset tag.");
infoGenesetTagCommand.AddArgument(genesetArgument);
genesetTagCommand.Add(infoGenesetTagCommand);

var initGenesetTagCommand =
    new Command("init", "This command initializes the filesystem structure for a geneset tag.");
initGenesetTagCommand.AddArgument(genesetArgument);
genesetTagCommand.Add(initGenesetTagCommand);

var refCommand = new Command("ref", "This command adds a reference to another geneset to the geneset tag.");
refCommand.AddArgument(genesetArgument);
refCommand.AddArgument(refArgument);
genesetTagCommand.Add(refCommand);


var addVMCommand = new Command("add-vm", "This command adds a exported Hyper-V VM to the geneset tag.");
addVMCommand.AddArgument(genesetArgument);
addVMCommand.AddArgument(vmExportArgument);
genesetTagCommand.AddCommand(addVMCommand);

var addVolumeCommand = new Command("add-volume", "This command adds a volume reference to the geneset tag.");
addVolumeCommand.AddArgument(genesetArgument);
addVolumeCommand.AddArgument(filePathArgument);
genesetTagCommand.AddCommand(addVolumeCommand);


var packCommand = new Command("pack", "This command packs the content of the local geneset tag into genes");
packCommand.AddArgument(genesetArgument);

genesetTagCommand.Add(packCommand);

var pushCommand = new Command("push", "This command uploads a geneset to eryph genepool");
pushCommand.AddArgument(genesetArgument);
pushCommand.AddOption(apiKeyOption);
genesetTagCommand.Add(pushCommand);

// ReSharper disable once StringLiteralTypo
var apiKeyCommand = new Command("apikey", "Commands to manage api key");
rootCommand.Add(apiKeyCommand);
var createApiKeyCommand = new Command("create", "This command creates a new api Key");
createApiKeyCommand.AddArgument(organizationArgument);
apiKeyCommand.Add(createApiKeyCommand);
var keyNameArgument = new Argument<string>("name", "name of the api key");
var permissionsArgument = new Argument<string[]>("permissions",
    () => ["Geneset.ReadWrite"], description: "permissions of the api key");
createApiKeyCommand.AddArgument(keyNameArgument);
createApiKeyCommand.AddArgument(permissionsArgument);

var deleteApiKeyCommand = new Command("delete", "This command deletes a api Key");
apiKeyCommand.Add(deleteApiKeyCommand);
deleteApiKeyCommand.AddArgument(organizationArgument);
var keyIdArgument = new Argument<string>("keyid", "id of the api key");
deleteApiKeyCommand.AddArgument(keyIdArgument);

// init commands
// ------------------------------
initGenesetCommand.SetHandler( context =>
{
    var isPublic = context.ParseResult.GetValueForOption(isPublicOption);
    var description = context.ParseResult.GetValueForOption(shortDescriptionOption);

    var genesetName = context.ParseResult.GetValueForArgument(genesetArgument);
    var genesetInfo = new GenesetInfo(genesetName, ".");
    if (genesetInfo.Exists())
        throw new EryphPackerUserException("Geneset is already initialized");

    genesetInfo.Create();
    if(File.Exists(Path.Combine(genesetInfo.GetGenesetPath(), "readme.md")))
    {
        AnsiConsole.WriteLine("Using file readme.md as content for geneset markdown description.");
    }
    else
    {
        File.WriteAllText(
            Path.Combine(genesetInfo.GetGenesetPath(), "readme.md"), $"# geneset {genesetInfo.GenesetName}\n");
        AnsiConsole.WriteLine("Generated skeleton readme.md for geneset.");

    }
    genesetInfo.SetMarkdownFile("readme.md");
    genesetInfo.SetIsPublic(isPublic);
    genesetInfo.SetShortDescription(!string.IsNullOrWhiteSpace(description) ? description : genesetInfo.Id);

    AnsiConsole.WriteLine("Geneset was initialized:");
    WriteJson(genesetInfo.ToString());
});

initGenesetTagCommand.SetHandler(context =>
{
    var genesetName = context.ParseResult.GetValueForArgument(genesetArgument);
    var genesetTagInfo = new GenesetTagInfo(genesetName, ".");
    if(genesetTagInfo.Exists())
        throw new EryphPackerUserException("Geneset tag is already initialized");

    var genesetInfo = new GenesetInfo(genesetName, ".");
    if(!genesetInfo.Exists())
        throw new EryphPackerUserException($"Geneset is not initialized. Initialize the geneset first with geneset init {genesetInfo.GenesetName}");


    genesetTagInfo.Create();
    AnsiConsole.WriteLine("Geneset tag was initialized:");
    WriteJson(genesetTagInfo.ToString());
});

// ref command
// ------------------------------
refCommand.SetHandler(context =>
{
    var genesetInfo = PrepareGeneSetTagCommand(context);
    var refPack = context.ParseResult.GetValueForArgument(refArgument);
    genesetInfo.SetReference(refPack);
    AnsiConsole.WriteLine("Reference was added to the geneset tag:");
    WriteJson(genesetInfo.ToString(false));
});


// info command
// ------------------------------
infoGenesetCommand.SetHandler(context =>
{
    var genesetInfo = PrepareGeneSetCommand(context);
    WriteJson(genesetInfo.ToString());
});

infoGenesetTagCommand.SetHandler(context =>
{
    var genesetTagInfo = PrepareGeneSetTagCommand(context);
    WriteJson(genesetTagInfo.ToString());
});


// add vm command
// ------------------------------
addVMCommand.SetHandler(async context =>
{
    var token = context.GetCancellationToken();
    var genesetTagInfo = PrepareGeneSetTagCommand(context);
    var vmExportDir = context.ParseResult.GetValueForArgument(vmExportArgument);

    // find the root of the exported vm
    vmExportDir = VMExport.FindExportRootDir(vmExportDir);


    var absolutePackPath = Path.GetFullPath(genesetTagInfo.GetGenesetPath());
    var (config, packableFiles) = VMExport.ExportToPackable(vmExportDir, token);
    VMExport.ReadMetadata(vmExportDir, genesetTagInfo);

    var catletGenerated = false;
    if (config != null)
    {
        var configYaml = CatletConfigYamlSerializer.Serialize(config);
        var catletYamlFilePath = Path.Combine(absolutePackPath, "catlet.yaml");

        if (configYaml != "{}") // do not serialize empty file
        {
            await File.WriteAllTextAsync(catletYamlFilePath, configYaml);
            catletGenerated = true;
        }
    }

    if(!catletGenerated)
        AnsiConsole.MarkupLine("[yellow]Catlet config is empty.[/]. You will have to a catlet.yaml file to the geneset.");

    ResetPackableFolder(absolutePackPath);
    WritePackableFiles(packableFiles, absolutePackPath);
    
    WriteJson(genesetTagInfo.ToString());
});

// pack catlet command
// ------------------------------
packCommand.SetHandler(async context =>
{
    var packedGenesetInfo = await AnsiConsole.Progress()
        .HideCompleted(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn { Spinner = Spinner.Known.Pong })
        .StartAsync(async progressContext =>
        {
            var isInteractive = AnsiConsole.Profile.Capabilities.Interactive;
            AnsiConsole.MarkupLine("Preparing catlet...");

            var token = context.GetCancellationToken();
            var genesetTagInfo = PrepareGeneSetTagCommand(context);
            var absoluteGenesetPath = Path.GetFullPath(genesetTagInfo.GetGenesetPath());

            // folder .pack is the temporary folder for packing
            var catletFile = Path.Combine(absoluteGenesetPath, "catlet.yaml");
            var packFolder = Path.Combine(absoluteGenesetPath, ".pack");
            if (!Directory.Exists(packFolder))
                Directory.CreateDirectory(packFolder);


            var packableFiles = await ReadPackableFiles(absoluteGenesetPath);
            string? parent = null;
            // pack catlet
            if (File.Exists(catletFile))
            {
                var fileLength = new FileInfo(catletFile).Length;
                if (fileLength > GeneModelDefaults.MaxYamlSourceBytes)
                    throw new EryphPackerUserException(
                        $"Catlet file is too large. Max size is {GeneModelDefaults.MaxYamlSourceBytes / 1024 / 1024} MiB.");

                var catletContent = File.ReadAllText(catletFile);
                var catletConfig = DeserializeCatletConfigString(catletContent);
                var validationResult = CatletConfigValidations.ValidateCatletConfig(catletConfig);
                _ = validationResult.ToEither()
                    .MapLeft(issues => Error.New("The catlet configuration is invalid.",
                        Error.Many(issues.Map(i => i.ToError()))))
                    .IfLeft(e => e.Throw());
                var configJson = CatletConfigJsonSerializer.Serialize(catletConfig);
                await File.WriteAllTextAsync(Path.Combine(packFolder, "catlet.json"), configJson);
                packableFiles.Add(new PackableFile(Path.Combine(packFolder, "catlet.json"),
                    "catlet.json", GeneType.Catlet,
                    Architectures.Any, // catlet is architecture independent
                    "catlet", false, catletContent));
                parent = catletConfig.Parent;
            }

            AnsiConsole.MarkupLine("Catlet [green]prepared[/]");

            // pack fodder
            AnsiConsole.MarkupLine("Preparing fodder...");

            var fodderDir = new DirectoryInfo(Path.Combine(absoluteGenesetPath, "fodder"));
            if (fodderDir.Exists)
            {
                await AddFodderFromDirectory(fodderDir, Architectures.Any);
                foreach (var hypervisorDir in fodderDir.GetDirectories())
                {
                    if (string.Equals(hypervisorDir.Name, Hypervisors.HyperV, StringComparison.OrdinalIgnoreCase))
                    {
                        await AddFodderFromDirectory(hypervisorDir, Architectures.HyperVAny);

                        foreach (var processorDir  in hypervisorDir.GetDirectories())
                        {
                            if (string.Equals(processorDir.Name, ProcessorTypes.Amd64,
                                    StringComparison.OrdinalIgnoreCase))
                                await AddFodderFromDirectory(processorDir, Architectures.HyperVAmd64);
                            else
                                AnsiConsole.MarkupLine($"Fodder dir contains [yellow]unknown processor type name '{processorDir.Name}'[/]");
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"Fodder dir contains [yellow]unknown hypervisor name '{hypervisorDir.Name}'[/]");

                    }
                }
            }

            AnsiConsole.MarkupLine("Fodder [green]prepared[/]");

            // created .packed folder with packing result
            var packedFolder = Path.Combine(absoluteGenesetPath, ".packed");
            if (Directory.Exists(packedFolder))
                Directory.Delete(packedFolder, true);
            Directory.CreateDirectory(packedFolder);
            genesetTagInfo.PreparePacking();
            File.Copy(Path.Combine(absoluteGenesetPath, "geneset-tag.json"),
                Path.Combine(packedFolder, "geneset-tag.json"));
            var packedGenesetInfo = new GenesetTagInfo(genesetTagInfo.GenesetTagName, packedFolder);

            packedGenesetInfo.SetParent(parent);

            var packingTasks = packableFiles.Select(pf => (
                Packable: pf,
                ProgressTask: progressContext.AddTask($"Packing gene {pf.GeneName} (arch: {pf.Architecture})", autoStart: false)
                )).ToList();

            // this will pack all genes in .packed folder
            foreach (var packingTask in packingTasks)
            {
                if (!isInteractive)
                    AnsiConsole.MarkupLineInterpolated($"Packing gene {packingTask.Packable.GeneName} (arch: {packingTask.Packable.Architecture})...");

                packingTask.ProgressTask.StartTask();
                var progress = new Progress<GenePackerProgress>();
                progress.ProgressChanged += (_, progressData) =>
                {
                    packingTask.ProgressTask.MaxValue = progressData.TotalBytes;
                    packingTask.ProgressTask.Value = progressData.ProcessedBytes;
                };
                var packedFile = await GenePacker.CreateGene(packingTask.Packable, packedFolder, progress, token);
                packingTask.ProgressTask.StopTask();
                
                if (!isInteractive)
                    AnsiConsole.MarkupLineInterpolated($"Gene {packingTask.Packable.GeneName} (arch: {packingTask.Packable.Architecture}) [green]packed[/]");

                packedGenesetInfo.AddGene(packingTask.Packable.GeneType, packingTask.Packable.GeneName, packedFile,
                    packingTask.Packable.Architecture);
            }

            // remove the temporary .pack folder
            if (Directory.Exists(packFolder))
                Directory.Delete(packFolder, true);

            return packedGenesetInfo;

            async Task AddFodderFromDirectory(DirectoryInfo directory, string architecture)
            {
                foreach (var fodderFile in directory.GetFiles("*.*").Where(x =>
                         {
                             var extension = Path.GetExtension(x.Name).ToLowerInvariant();
                             return extension is ".yaml" or ".yml";
                         }))
                {
                    if (fodderFile.Length > GeneModelDefaults.MaxYamlSourceBytes)
                        throw new EryphPackerUserException(
                            $"Fodder file '{fodderFile.Name}' is too large. Max size is {GeneModelDefaults.MaxYamlSourceBytes / 1024 / 1024} MiB.");

                    var fodderContent = File.ReadAllText(fodderFile.FullName);
                    var fodderConfig = DeserializeFodderConfigString(fodderContent);
                    var validationResult = FodderGeneConfigValidations.ValidateFodderGeneConfig(fodderConfig);
                    validationResult.ToEither()
                        .MapLeft(issues => Error.New($"The fodder configuration '{fodderFile.Name}' is invalid.",
                            Error.Many(issues.Map(i => i.ToError()))))
                        .IfLeft(e => e.Throw());
                    var fodderJson = FodderGeneConfigJsonSerializer.Serialize(fodderConfig);
                    var fodderPackFolder = Path.Combine(packFolder, "fodder");
                    if (!Directory.Exists(fodderPackFolder))
                        Directory.CreateDirectory(fodderPackFolder);

                    var fodderJsonFile = Path.Combine(fodderPackFolder, $"{fodderConfig.Name}.json");
                    await File.WriteAllTextAsync(fodderJsonFile, fodderJson);
                    packableFiles.Add(new PackableFile(fodderJsonFile,
                        $"{fodderConfig.Name}.json", GeneType.Fodder,
                        architecture,
                        fodderConfig.Name!, false, fodderContent));
                }
            }

            static CatletConfig DeserializeCatletConfigString(string configString)
            {
                configString = configString.Trim();
                configString = configString.Replace("\r\n", "\n");

                return CatletConfigYamlSerializer.Deserialize(configString);
            }

            static FodderGeneConfig DeserializeFodderConfigString(string configString)
            {
                configString = configString.Trim();
                configString = configString.Replace("\r\n", "\n");

                return FodderGeneConfigYamlSerializer.Deserialize(configString);
            }
        });

    AnsiConsole.MarkupLineInterpolated($"Geneset '{packedGenesetInfo.GenesetTagName}' [green]successfully packed[/]");
    WriteJson(packedGenesetInfo.ToString());
});


// push command
// ------------------------------
pushCommand.SetHandler(async context =>
{
    var token = context.GetCancellationToken();
    var genesetInfo = PrepareGeneSetCommand(context);
    var genesetTagInfo = PrepareGeneSetTagCommand(context);
    var isInteractive = AnsiConsole.Profile.Capabilities.Interactive;
    var apiKey = context.ParseResult.GetValueForOption(apiKeyOption);

    var packedFolder = Path.Combine(genesetTagInfo.GetGenesetPath(), ".packed");
    if (!Directory.Exists(packedFolder))
        throw new EryphPackerUserException($"Geneset tag '{genesetTagInfo.GenesetTagName}' is not packed. Use 'pack' command first.");

    genesetTagInfo = new GenesetTagInfo(genesetTagInfo.GenesetTagName, packedFolder);
    
    var credential = await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots2)
        .SpinnerStyle(Style.Parse("green bold"))
        .StartAsync("Authenticating to genepool...",async statusContext =>
        {
            statusContext.Status = "Authenticated to genepool.";
            statusContext.Refresh();
            return await AuthProvider.GetCredential(apiKey, stagingAuthority);
        });

    var genePoolClient = credential.ApiKey != null
        ? new GenePoolClient(genePoolUri, credential.ApiKey, clientOptions)
        : new GenePoolClient(genePoolUri, credential.Token!, clientOptions);

    var genesetClient = genePoolClient.GetGenesetClient(genesetTagInfo.Organization, genesetTagInfo.Id);
    var tagClient = genesetClient.GetGenesetTagClient(genesetTagInfo.Tag);

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots2)
        .SpinnerStyle(Style.Parse("green bold"))
        .StartAsync($"Checking geneset tag {genesetTagInfo.GenesetTagName}", async statusContext =>
        {
            var markdownContent = genesetInfo.GetMarkdownContent();

            if (!await genesetClient.ExistsAsync())
            {
                statusContext.Status = $"Creating geneset {genesetInfo.GenesetName}";
                statusContext.Refresh();
                await genesetClient.CreateAsync(genesetInfo.ManifestData.Public ?? false,
                    genesetInfo.ManifestData.ShortDescription,
                    genesetInfo.ManifestData.Description,
                    markdownContent, 
                    genesetInfo.ManifestData.Metadata,
                    cancellationToken: token
                );
            }
            else
            {
                statusContext.Status = $"Updating geneset {genesetInfo.GenesetName}";
                statusContext.Refresh();
                var geneset = await genesetClient.GetAsync(
                    new GetGenesetRequestOptions{ NoCache = true},
                    cancellationToken: token);
                await genesetClient.UpdateAsync(geneset?.Public ?? genesetInfo.ManifestData.Public,
                    genesetInfo.ManifestData.ShortDescription,
                    genesetInfo.ManifestData.Description,
                    markdownContent,
                    genesetInfo.ManifestData.Metadata,
                    geneset?.ETag,
                    cancellationToken: token
                );
            }

            if (!genesetTagInfo.IsReference() && await tagClient.ExistsAsync())
                throw new EryphPackerUserException($"Geneset tag {genesetTagInfo.GenesetTagName} already exists on genepool. Tags can only be updated when they are references.");
        });


    var packDir = new DirectoryInfo(genesetTagInfo.GetGenesetPath());
    var allGenes = genesetTagInfo.GetAllGeneNames().ToArray();

    await AnsiConsole.Progress()
        .HideCompleted(false)
        .Columns(new TaskGeneColumn(), 
            new TaskGeneCountColumn(), 
            new TaskDescriptionColumn{Alignment = Justify.Left}, 
            new ProgressBarColumn(), 
            new PercentageColumn(), 
            new SpinnerColumn{Spinner = Spinner.Known.Pong}).StartAsync(async progressContext =>
        {
            var count = 0;
            foreach (var geneName in allGenes)
            {
                count++;
                var genePath = new DirectoryInfo(Path.Combine(packDir.FullName, geneName));
                if (!genePath.Exists)
                    throw new Exception($"Gene {geneName} not found in directory {packDir.FullName}");

                var progressLock = new object();
                var progress = new Progress<GeneUploadProgress>();
                ProgressTask? progressTask = null;
                var state = new GeneUploadTask
                {
                    GeneName = $"Gene {geneName[..12]}",
                    No = count,
                    Total = allGenes.Length
                };
                var baseDescription = !isInteractive
                    ? $"Gene {geneName[..12]}: {count} of {allGenes.Length} - "
                    : "";

                progress.ProgressChanged += (_, progressData) =>
                {
                    var totalSizeScale = "Bytes";
                    var totalSize = progressData.TotalMissingSize * 1d;
                    var uploadedSize = progressData.TotalUploadedSize * 1d;
                    if (totalSize > 10 * 1024)
                    {
                        totalSizeScale = "KiB";
                        totalSize /= 1024d;
                        uploadedSize /= 1024d;
                    }

                    if (totalSize > 10 * 1024)
                    {
                        totalSizeScale = "MiB";
                        totalSize /= 1024d;
                        uploadedSize /= 1024d;
                    }

                    if (totalSize > 10 * 1024)
                    {
                        totalSizeScale = "GiB";
                        totalSize /= 1024d;
                        uploadedSize /= 1024d;
                    }

                    lock(progressLock)
                    {
                        // ReSharper disable once AccessToModifiedClosure
                        if (progressTask == null)
                            progressTask =
                                progressContext.AddTask(
                                    $"{baseDescription}0.00 {totalSizeScale} of {totalSize:0.00} {totalSizeScale})",
                                    maxValue: totalSize);

                        progressTask.State.Update<GeneUploadTask>("gene", _ => state);
                        progressTask.Value=uploadedSize;
                        progressTask.Description =
                            $"{baseDescription}{uploadedSize:0.00} {totalSizeScale} of {totalSize:0.00} {totalSizeScale}";
                    }
                };

                await genePoolClient.CreateGeneFromPathAsync(
                    genesetTagInfo.GenesetTagName, genePath.FullName, token, progress: progress);

                if (progressTask == null && isInteractive)
                {
                    
                    progressTask =
                        progressContext.AddTask(
                            $"{baseDescription}upload completed");
                    progressTask.Value = 100;
                    progressTask.State.Update<GeneUploadTask>("gene", _ => state);

                }

                if (progressTask != null)
                {
                    progressTask.Description =
                        $"{baseDescription}upload completed";
                    progressTask?.StopTask();
                }
            }
        });

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots2)
        .SpinnerStyle(Style.Parse("green bold"))
        .StartAsync($"Creating geneset tag {genesetTagInfo.GenesetTagName}", async statusContext =>
        {
            await tagClient.CreateAsync(genesetTagInfo.ManifestData, cancellationToken: token);

        });

    AnsiConsole.WriteLine($"Geneset tag '{genesetTagInfo.ManifestData.Geneset}' successfully pushed to genepool.");
});

// api key management
createApiKeyCommand.SetHandler(async (context) =>
{
    var token = context.GetCancellationToken();
    var organization = context.ParseResult.GetValueForArgument(organizationArgument);
    var keyName = context.ParseResult.GetValueForArgument(keyNameArgument);
    var permissions = context.ParseResult.GetValueForArgument(permissionsArgument);

    var credential = await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots2)
        .SpinnerStyle(Style.Parse("green bold"))
        .StartAsync("Authenticating to genepool...", async statusContext =>
        {
            statusContext.Status = "Authenticated to genepool.";
            statusContext.Refresh();
            return await AuthProvider.GetCredential(null, stagingAuthority);
        });


    var genePoolClient = new GenePoolClient(genePoolUri, credential.Token!, clientOptions);
    var orgClient = genePoolClient.GetOrganizationClient(organization);

    var apiKeyResponseJson = await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots2)
        .SpinnerStyle(Style.Parse("green bold"))
        .StartAsync("Creating api key", async _ =>
        {
            var apiKeyResponse = await orgClient.CreateApiKeyAsync(keyName, permissions, cancellationToken: token);
            var apiKeyResponseJson =
                JsonSerializer.Serialize(apiKeyResponse, new JsonSerializerOptions(GeneModelDefaults.SerializerOptions)
                {
                    WriteIndented = true
                });
            return apiKeyResponseJson;
        });

    WriteJson(apiKeyResponseJson);
});

deleteApiKeyCommand.SetHandler(async (context) =>
{
    var token = context.GetCancellationToken();
    var organization = context.ParseResult.GetValueForArgument(organizationArgument);
    var keyId = context.ParseResult.GetValueForArgument(keyIdArgument);

    var credential = await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots2)
        .SpinnerStyle(Style.Parse("green bold"))
        .StartAsync("Authenticating to genepool...", async statusContext =>
        {
            statusContext.Status = "Authenticated to genepool.";
            statusContext.Refresh();
            return await AuthProvider.GetCredential(null, stagingAuthority);
        });


    var genePoolClient = new GenePoolClient(genePoolUri, credential.Token!, clientOptions);
    var apiKeyClient = genePoolClient.GetApiKeyClient(organization, keyId);

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots2)
        .SpinnerStyle(Style.Parse("green bold"))
        .StartAsync("Deleting api key", async _ =>
        {
            await apiKeyClient.DeleteAsync(cancellationToken: token);
        });

});

var commandLineBuilder = new CommandLineBuilder(rootCommand);
commandLineBuilder.UseDefaults();
commandLineBuilder.UseAnsiTerminalWhenAvailable();
commandLineBuilder.UseExceptionHandler((ex, context) =>
{
    if (ex is OperationCanceledException)
    {
        AnsiConsole.MarkupLine("[yellow]The operation was canceled[/]");
    }
    else if (ex is EryphPackerUserException)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
    }
    else if (ex is ErrorException eex)
    {
        var error = eex.ToError();

        Grid createGrid() => new Grid()
            .AddColumn(new GridColumn { Width = 2 })
            .AddColumn();

        Grid addRow(Grid grid, IRenderable renderable) =>
            grid.AddRow(new Markup(""), renderable);

        Grid addToGrid(Grid grid, Error error) => error switch
        {
            ManyErrors me => me.Errors.Fold(grid, addToGrid),
            Exceptional ee => addRow(grid, ee.ToException().GetRenderable()),
            _ => addRow(grid, new Text(error.Message))
                .Apply(g => error.Inner.Match(
                    Some: ie => addRow(g, addToGrid(createGrid(), ie)),
                    None: () => g)),
        };

        AnsiConsole.Write(new Rows(
            new Markup("[red]The operation failed with following error(s):[/]"),
            addToGrid(createGrid(), error)));
        AnsiConsole.WriteLine();
    }
    else
    {
        AnsiConsole.MarkupLine("[red]The operation failed with the following error:[/]");
        AnsiConsole.WriteException(ex);
    }

    context.ExitCode = 1;
});
commandLineBuilder.AddMiddleware(context =>
{
    var workdir = context.ParseResult.GetValueForOption(workDirOption);

    if (workdir?.FullName != null)
        Directory.SetCurrentDirectory(workdir.FullName);
});

var parser = commandLineBuilder.Build();

return await parser.InvokeAsync(args);

void WritePackableFiles(IEnumerable<PackableFile> files, string genesetPath)
{
    var packFolder = Path.Combine(genesetPath, ".pack");
    if (!Directory.Exists(packFolder))
        Directory.CreateDirectory(packFolder);
    
    var packableJson = JsonSerializer.Serialize(files, GeneModelDefaults.SerializerOptions);
    var packableJsonFilePath = Path.Combine(packFolder, "packable.json");
    File.WriteAllText(packableJsonFilePath, packableJson);
}

void ResetPackableFolder(string genesetPath)
{
    var packFolder = Path.Combine(genesetPath, ".pack");
    if (Directory.Exists(packFolder))
        Directory.Delete(packFolder, true);
}

async Task<List<PackableFile>> ReadPackableFiles(string genesetPath)
{
    var packFolder = Path.Combine(genesetPath, ".pack");

    if (File.Exists(Path.Combine(packFolder, "packable.json")))
    {
        var packableJson = await File.ReadAllTextAsync(Path.Combine(packFolder, "packable.json"));
        return JsonSerializer.Deserialize<List<PackableFile>>(packableJson, GeneModelDefaults.SerializerOptions) ?? [];
    }

    return [];
}

GenesetTagInfo PrepareGeneSetTagCommand(InvocationContext context)
{
    var genesetName = context.ParseResult.GetValueForArgument(genesetArgument);

    var genesetInfo = new GenesetTagInfo(genesetName, ".");
    if (!genesetInfo.Exists())
        throw new EryphPackerUserException($"Geneset tag {genesetName} not found");

    genesetInfo.Validate();
    return genesetInfo;
}

GenesetInfo PrepareGeneSetCommand(InvocationContext context)
{
    var genesetName = context.ParseResult.GetValueForArgument(genesetArgument);

    var genesetInfo = new GenesetInfo(genesetName, ".");
    if (!genesetInfo.Exists())
        throw new EryphPackerUserException($"Geneset {genesetName} not found");

    genesetInfo.Validate();
    return genesetInfo;
}

void WriteJson(string json)
{
    // Wrap the JsonText in Rows to ensure a line break at the end
    AnsiConsole.Write(new Rows(new JsonText(json).StringColor(Color.Teal)));
}
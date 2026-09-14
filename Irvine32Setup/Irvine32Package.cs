using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Irvine32Setup;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Irvine32 Setup", "Configures Irvine32 library for MASM projects.", "1.4")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideUIContextRule(UiContextGuidString,
    name: "AutoLoadOnSolution",
    expression: "SolutionExists",
    termNames: new[] { "SolutionExists" },
    termValues: new[] { VSConstants.UICONTEXT.SolutionExists_string })]
[ProvideAutoLoad(UiContextGuidString, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
[System.Runtime.InteropServices.Guid(PackageGuidString)]
public sealed class Irvine32Package : AsyncPackage
{
    public const string PackageGuidString = "a4d36b69-0e2d-4a88-9b76-8ccf7d45c7b1";
    public const string UiContextGuidString = "486b2f3d-4a44-48c6-b0b1-5de84687c172";
    private const int ConfigureCommandId = 0x0100;
    private const int AddAsmFileCommandId = 0x0200;
    private static readonly Guid CommandSet = new("8f5f49c2-3b7c-4d6e-9d5e-4db7b07e5f38");

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                var configureCmdId = new CommandID(CommandSet, ConfigureCommandId);
                commandService.AddCommand(new OleMenuCommand(ConfigureIrvine32, configureCmdId));

                var addAsmCmdId = new CommandID(CommandSet, AddAsmFileCommandId);
                commandService.AddCommand(new OleMenuCommand(AddIrvineAsmFile, addAsmCmdId));
            }

            // Listen for solution fully loaded event
            KnownUIContexts.SolutionExistsAndFullyLoadedContext.UIContextChanged += (s, e) =>
            {
                _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (KnownUIContexts.SolutionExistsAndFullyLoadedContext.IsActive)
                        {
                            await CheckAndPromptOnSolutionOpenAsync();
                        }
                    }
                    catch
                    {
                        // Ignore non-fatal UI context/solution check errors during async events
                    }
                });
            };

            if (KnownUIContexts.SolutionExistsAndFullyLoadedContext.IsActive)
            {
                await CheckAndPromptOnSolutionOpenAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Irvine32Package initialization error: {ex}");
        }
    }

    private void ConfigureIrvine32(object sender, EventArgs e)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await ConfigureIrvine32Async(isManualInvocation: true);
        });
    }

    private void AddIrvineAsmFile(object sender, EventArgs e)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await AddIrvineAsmFileAsync();
        });
    }

    private async Task CheckAndPromptOnSolutionOpenAsync()
    {
        try
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte == null) return;

            var vcxprojPaths = GetVcxprojProjects(dte);
            if (vcxprojPaths.Count == 0) return;

            var unconfigured = vcxprojPaths.Where(p => !IsProjectConfigured(p)).ToList();
            if (unconfigured.Count == 0) return;

            var response = MessageBox.Show(
                "One or more C++ projects in this solution require Irvine32 / MASM setup.\n\n" +
                "Would you like to configure Irvine32 and MASM now?",
                "Irvine32 Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (response == DialogResult.Yes)
            {
                await ConfigureIrvine32Async(isManualInvocation: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CheckAndPromptOnSolutionOpenAsync error: {ex}");
        }
    }

    private async Task ConfigureIrvine32Async(bool isManualInvocation)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        if (dte == null) return;

        var vcxprojPaths = GetVcxprojProjects(dte);
        if (vcxprojPaths.Count == 0)
        {
            if (isManualInvocation)
            {
                MessageBox.Show(
                    "No C++ (.vcxproj) projects were found in the current solution.\nPlease open a solution containing a C++ project first.",
                    "Irvine32 Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        string? solutionDir = null;
        try
        {
            if (dte.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
            {
                solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
            }
        }
        catch { }

        string? chosenPath = null;
        var detectedPath = DetectIrvine32Path(solutionDir);

        if (!string.IsNullOrEmpty(detectedPath))
        {
            var useDetected = MessageBox.Show(
                $"Found Irvine32 library at:\n'{detectedPath}'\n\nWould you like to use this location?\n\n(Select 'No' to browse for another folder)",
                "Irvine32 Setup - Path Detected",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (useDetected == DialogResult.Cancel)
                return;

            if (useDetected == DialogResult.Yes)
            {
                chosenPath = detectedPath;
            }
        }

        if (string.IsNullOrEmpty(chosenPath))
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Select the folder containing Irvine32.inc and Irvine32.lib.",
                ShowNewFolderButton = false
            };

            if (!string.IsNullOrEmpty(detectedPath) && Directory.Exists(detectedPath))
            {
                picker.SelectedPath = detectedPath;
            }

            if (picker.ShowDialog() == DialogResult.OK)
            {
                chosenPath = picker.SelectedPath;
            }
            else
            {
                if (string.IsNullOrEmpty(detectedPath))
                {
                    chosenPath = await TryDownloadAndInstallIrvineAsync();
                }
            }
        }

        if (string.IsNullOrEmpty(chosenPath) || !IsValidIrvineDirectory(chosenPath))
        {
            if (isManualInvocation && !string.IsNullOrEmpty(chosenPath))
            {
                MessageBox.Show(
                    "The selected folder must contain both 'Irvine32.inc' and 'Irvine32.lib'.",
                    "Irvine32 Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return;
        }

        try
        {
            dte.ExecuteCommand("File.SaveAll");
        }
        catch { }

        var updated = 0;
        foreach (var projectPath in vcxprojPaths)
        {
            try
            {
                ConfigureProject(projectPath, chosenPath!);
                updated++;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to configure '{Path.GetFileName(projectPath)}':\n{ex.Message}",
                    "Irvine32 Setup Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        TryEnsureWin32Platform(dte);

        MessageBox.Show(
            $"Successfully configured Irvine32 for {updated} project(s)!\n\n" +
            $"Irvine32 Path: {chosenPath}\n\n" +
            "Important Reminders:\n" +
            "1. Irvine32 is a 32-bit library. Build with the 'x86' (Win32) configuration.\n" +
            "2. If Visual Studio asks to reload the project, choose 'Reload'.",
            "Irvine32 Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task AddIrvineAsmFileAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        if (dte == null) return;

        var targetProjectPath = GetSelectedOrPrimaryVcxproj(dte);
        if (string.IsNullOrEmpty(targetProjectPath) || !File.Exists(targetProjectPath))
        {
            MessageBox.Show(
                "Please select or open a C++ (.vcxproj) project in Solution Explorer first.",
                "Irvine32 Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        string projectPath = targetProjectPath!;
        var inputName = ShowFileNameDialog("main.asm");
        if (string.IsNullOrWhiteSpace(inputName))
            return;

        string fileName = inputName!;
        if (!fileName.EndsWith(".asm", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".asm";
        }

        var projectDir = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(projectDir))
            return;

        var filePath = Path.Combine(projectDir, fileName);

        if (File.Exists(filePath))
        {
            var overwrite = MessageBox.Show(
                $"File '{fileName}' already exists in the project directory.\n\nDo you want to overwrite it?",
                "File Exists",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (overwrite != DialogResult.Yes)
                return;
        }

        const string asmTemplate =
@"; =========================================================================
; Title: Irvine32 MASM Starter Template
; Description: Demonstration program using the Irvine32 library.
; =========================================================================
INCLUDE Irvine32.inc

.data
    welcomeMsg BYTE ""========================================"", 0dh, 0ah
               BYTE "" Hello, World from Irvine32 MASM!       "", 0dh, 0ah
               BYTE ""========================================"", 0dh, 0ah, 0
    val1       DWORD 15
    val2       DWORD 25
    sumResult  DWORD ?

.code
main PROC
    ; Display greeting message
    mov edx, OFFSET welcomeMsg
    call WriteString
    call Crlf

    ; Perform demonstration arithmetic
    mov eax, val1
    add eax, val2
    mov sumResult, eax

    ; Display register states
    call DumpRegs

    exit
main ENDP
END main
";

        try
        {
            File.WriteAllText(filePath, asmTemplate);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create '{fileName}':\n{ex.Message}", "File Creation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        AddAsmToProjectFile(projectPath, fileName);

        if (!IsProjectConfigured(projectPath))
        {
            var askConfig = MessageBox.Show(
                $"Project '{Path.GetFileName(projectPath)}' is not yet configured for Irvine32.\n\nWould you like to configure Irvine32 now?",
                "Configure Irvine32",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (askConfig == DialogResult.Yes)
            {
                await ConfigureIrvine32Async(isManualInvocation: false);
            }
        }

        TryEnsureWin32Platform(dte);

        try
        {
            dte.ItemOperations.OpenFile(filePath);
        }
        catch { }
    }

    private static string? ShowFileNameDialog(string defaultName)
    {
        using var form = new Form
        {
            Width = 420,
            Height = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "Add Irvine32 Assembly File",
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var label = new Label { Left = 20, Top = 15, Width = 360, Text = "Assembly file name (with .asm extension):" };
        var textBox = new TextBox { Left = 20, Top = 40, Width = 360, Text = defaultName };
        var btnOk = new Button { Text = "Add", Left = 200, Width = 85, Top = 75, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Left = 295, Width = 85, Top = 75, DialogResult = DialogResult.Cancel };

        form.Controls.AddRange(new Control[] { label, textBox, btnOk, btnCancel });
        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        return form.ShowDialog() == DialogResult.OK ? textBox.Text.Trim() : null;
    }

    private static void AddAsmToProjectFile(string projectPath, string fileName)
    {
        var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root == null) return;
        var ns = root.Name.Namespace;

        EnsureMasmBuildCustomizations(document, ns);
        ConvertAsmFilesToMasm(document, ns);

        bool alreadyIncluded = root.Elements(ns + "ItemGroup")
            .Elements(ns + "MASM")
            .Any(m => string.Equals((string?)m.Attribute("Include"), fileName, StringComparison.OrdinalIgnoreCase));

        if (!alreadyIncluded)
        {
            var masmGroup = root.Elements(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "MASM").Any());

            if (masmGroup == null)
            {
                masmGroup = new XElement(ns + "ItemGroup");
                var targets = root.Elements(ns + "Import").LastOrDefault();
                if (targets != null) targets.AddBeforeSelf(masmGroup);
                else root.Add(masmGroup);
            }

            var masmItem = new XElement(ns + "MASM",
                new XAttribute("Include", fileName),
                new XElement(ns + "FileType", "Document"));

            masmGroup.Add(masmItem);
            document.Save(projectPath);
        }
    }

    private static string? GetSelectedOrPrimaryVcxproj(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (dte.SelectedItems != null && dte.SelectedItems.Count > 0)
            {
                foreach (EnvDTE.SelectedItem item in dte.SelectedItems)
                {
                    if (item.Project != null)
                    {
                        var fn = item.Project.FullName;
                        if (!string.IsNullOrEmpty(fn) && fn.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) && File.Exists(fn))
                        {
                            return fn;
                        }
                    }
                }
            }
        }
        catch { }

        var all = GetVcxprojProjects(dte);
        return all.FirstOrDefault();
    }

    private static void TryEnsureWin32Platform(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var solutionBuild = dte.Solution?.SolutionBuild;
            if (solutionBuild == null) return;

            var activeConfig = solutionBuild.ActiveConfiguration as SolutionConfiguration2;
            if (activeConfig == null) return;

            var currentPlatform = activeConfig.PlatformName;
            if (!string.Equals(currentPlatform, "x86", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentPlatform, "Win32", StringComparison.OrdinalIgnoreCase))
            {
                EnvDTE.SolutionConfiguration? targetConfig = null;
                foreach (EnvDTE.SolutionConfiguration cfg in solutionBuild.SolutionConfigurations)
                {
                    if (cfg is SolutionConfiguration2 cfg2)
                    {
                        if ((string.Equals(cfg2.PlatformName, "x86", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(cfg2.PlatformName, "Win32", StringComparison.OrdinalIgnoreCase)) &&
                            string.Equals(cfg2.Name, activeConfig.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            targetConfig = cfg;
                            break;
                        }
                    }
                }

                if (targetConfig != null)
                {
                    var ask = MessageBox.Show(
                        $"The active solution platform is currently set to '{currentPlatform}'.\n\n" +
                        "Irvine32 is a 32-bit library requiring x86 (Win32).\n" +
                        "Would you like to switch the active solution platform to x86 now?",
                        "Switch Platform to x86",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (ask == DialogResult.Yes)
                    {
                        targetConfig.Activate();
                    }
                }
            }
        }
        catch { }
    }

    private static string? DetectIrvine32Path(string? solutionDir)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(solutionDir))
        {
            candidates.Add(Path.Combine(solutionDir, "Irvine"));
            candidates.Add(Path.Combine(solutionDir, "Irvine32"));
            candidates.Add(Path.Combine(solutionDir, "Irvine32-master"));
        }

        var env = Environment.GetEnvironmentVariable("IRVINE") ?? Environment.GetEnvironmentVariable("IRVINE32");
        if (!string.IsNullOrEmpty(env))
        {
            candidates.Add(env);
        }

        candidates.Add(@"C:\Irvine");
        candidates.Add(@"C:\Irvine32");
        candidates.Add(@"C:\Irvine32-master");
        candidates.Add(@"D:\Irvine");
        candidates.Add(@"D:\Irvine32");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "Downloads", "Irvine"));
            candidates.Add(Path.Combine(userProfile, "Downloads", "Irvine32"));
            candidates.Add(Path.Combine(userProfile, "Downloads", "Irvine32-master"));
            candidates.Add(Path.Combine(userProfile, "Desktop", "Irvine"));
            candidates.Add(Path.Combine(userProfile, "Desktop", "Irvine32"));
        }

        foreach (var dir in candidates)
        {
            if (IsValidIrvineDirectory(dir))
            {
                return Path.GetFullPath(dir);
            }
        }

        return null;
    }

    private static bool IsValidIrvineDirectory(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            return Directory.Exists(path) &&
                   File.Exists(Path.Combine(path, "Irvine32.inc")) &&
                   File.Exists(Path.Combine(path, "Irvine32.lib"));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> TryDownloadAndInstallIrvineAsync()
    {
        var ask = MessageBox.Show(
            "Irvine32 library was not found in common locations.\n\n" +
            "Would you like to automatically download and install Irvine32 to 'C:\\Irvine'?",
            "Download Irvine32",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (ask != DialogResult.Yes)
            return null;

        var targetDir = @"C:\Irvine";
        var tempZip = Path.Combine(Path.GetTempPath(), "Irvine32_Download.zip");

        try
        {
            Directory.CreateDirectory(targetDir);

            using (var client = new WebClient())
            {
                try
                {
                    await client.DownloadFileTaskAsync(new Uri("https://github.com/kipirvine/Irvine32/archive/refs/heads/master.zip"), tempZip);
                }
                catch
                {
                    await client.DownloadFileTaskAsync(new Uri("http://kipirvine.com/asm/examples/Irvine.zip"), tempZip);
                }
            }

            if (File.Exists(tempZip))
            {
                ZipFile.ExtractToDirectory(tempZip, targetDir);
                try { File.Delete(tempZip); } catch { }

                if (IsValidIrvineDirectory(targetDir))
                    return targetDir;

                foreach (var sub in Directory.GetDirectories(targetDir))
                {
                    if (IsValidIrvineDirectory(sub))
                        return sub;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Auto-download could not be completed:\n{ex.Message}\n\nPlease install Irvine32 manually.", "Download Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return null;
    }

    private static List<string> GetVcxprojProjects(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var list = new List<string>();
        if (dte.Solution?.Projects == null) return list;

        foreach (EnvDTE.Project project in dte.Solution.Projects)
        {
            CollectProjectPaths(project, list);
        }
        return list;
    }

    private static void CollectProjectPaths(EnvDTE.Project project, List<string> list)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project == null) return;

        try
        {
            var fullName = project.FullName;
            if (!string.IsNullOrEmpty(fullName) &&
                fullName.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullName))
            {
                list.Add(fullName);
                return;
            }

            if (project.ProjectItems != null)
            {
                foreach (EnvDTE.ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject != null)
                    {
                        CollectProjectPaths(item.SubProject, list);
                    }
                }
            }
        }
        catch { }
    }

    private static bool IsProjectConfigured(string projectPath)
    {
        try
        {
            if (!File.Exists(projectPath)) return false;
            var document = XDocument.Load(projectPath);
            var root = document.Root;
            if (root == null) return false;

            var ns = root.Name.Namespace;

            // If any .asm file is trapped in None or ClCompile, it needs configuration
            bool hasUnconfiguredAsm = root.Elements(ns + "ItemGroup")
                .Elements()
                .Any(e => (e.Name == ns + "None" || e.Name == ns + "ClCompile") &&
                          ((string?)e.Attribute("Include"))?.EndsWith(".asm", StringComparison.OrdinalIgnoreCase) == true);
            if (hasUnconfiguredAsm) return false;

            var definitionGroups = root.Elements(ns + "ItemDefinitionGroup");
            foreach (var group in definitionGroups)
            {
                var masm = group.Element(ns + "MASM");
                var include = (string?)masm?.Element(ns + "IncludePaths");
                if (include != null && include.IndexOf("Irvine", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static void ConfigureProject(string projectPath, string irvinePath)
    {
        var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root == null) return;
        var ns = root.Name.Namespace;

        EnsureMasmBuildCustomizations(document, ns);
        ConvertAsmFilesToMasm(document, ns);

        var definitionGroups = root.Elements(ns + "ItemDefinitionGroup").ToList();
        if (definitionGroups.Count == 0)
        {
            var import = root.Elements(ns + "Import").LastOrDefault();
            var group = new XElement(ns + "ItemDefinitionGroup");
            if (import is null) root.Add(group); else import.AddBeforeSelf(group);
            definitionGroups.Add(group);
        }

        foreach (var group in definitionGroups)
        {
            var masm = GetOrCreate(group, ns + "MASM");
            SetValue(masm, ns + "IncludePaths", $"{irvinePath};%(IncludePaths)");

            var condition = (string?)group.Attribute("Condition") ?? string.Empty;
            if (condition.IndexOf("Win32", StringComparison.OrdinalIgnoreCase) >= 0 ||
                condition.IndexOf("x86", StringComparison.OrdinalIgnoreCase) >= 0 ||
                condition.Length == 0)
            {
                var link = GetOrCreate(group, ns + "Link");
                SetValue(link, ns + "AdditionalLibraryDirectories", $"{irvinePath};%(AdditionalLibraryDirectories)");
                SetValue(link, ns + "AdditionalDependencies", "Irvine32.lib;%(AdditionalDependencies)");

                var subSystem = link.Element(ns + "SubSystem");
                if (subSystem == null || string.IsNullOrWhiteSpace(subSystem.Value))
                {
                    link.Add(new XElement(ns + "SubSystem", "Console"));
                }
            }
        }

        document.Save(projectPath);
    }

    private static void ConvertAsmFilesToMasm(XDocument document, XNamespace ns)
    {
        var root = document.Root;
        if (root == null) return;

        var itemsToConvert = root.Elements(ns + "ItemGroup")
            .Elements()
            .Where(e => (e.Name == ns + "None" || e.Name == ns + "ClCompile" || e.Name == ns + "ClInclude") &&
                        ((string?)e.Attribute("Include"))?.EndsWith(".asm", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (itemsToConvert.Count == 0) return;

        var masmGroup = root.Elements(ns + "ItemGroup")
            .FirstOrDefault(g => g.Elements(ns + "MASM").Any());

        if (masmGroup == null)
        {
            masmGroup = new XElement(ns + "ItemGroup");
            var targets = root.Elements(ns + "Import").LastOrDefault();
            if (targets != null) targets.AddBeforeSelf(masmGroup);
            else root.Add(masmGroup);
        }

        foreach (var item in itemsToConvert)
        {
            var include = (string?)item.Attribute("Include");
            item.Remove();

            if (!string.IsNullOrEmpty(include))
            {
                bool alreadyInMasm = masmGroup.Elements(ns + "MASM")
                    .Any(m => string.Equals((string?)m.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase));
                if (!alreadyInMasm)
                {
                    masmGroup.Add(new XElement(ns + "MASM",
                        new XAttribute("Include", include),
                        new XElement(ns + "FileType", "Document")));
                }
            }
        }
    }

    private static void EnsureMasmBuildCustomizations(XDocument document, XNamespace ns)
    {
        var root = document.Root;
        if (root == null) return;

        const string masmProps = @"$(VCTargetsPath)\BuildCustomizations\masm.props";
        const string masmTargets = @"$(VCTargetsPath)\BuildCustomizations\masm.targets";

        var settingsGroup = root.Elements(ns + "ImportGroup")
            .FirstOrDefault(g => (string?)g.Attribute("Label") == "ExtensionSettings");
        if (settingsGroup == null)
        {
            settingsGroup = new XElement(ns + "ImportGroup", new XAttribute("Label", "ExtensionSettings"));
            var cppProps = root.Elements(ns + "Import")
                .FirstOrDefault(i => ((string?)i.Attribute("Project"))?.Contains("Microsoft.Cpp.props") == true);
            if (cppProps != null)
                cppProps.AddAfterSelf(settingsGroup);
            else
                root.Add(settingsGroup);
        }

        bool hasMasmProps = settingsGroup.Elements(ns + "Import")
            .Any(i => string.Equals((string?)i.Attribute("Project"), masmProps, StringComparison.OrdinalIgnoreCase));
        if (!hasMasmProps)
        {
            settingsGroup.Add(new XElement(ns + "Import", new XAttribute("Project", masmProps)));
        }

        var targetsGroup = root.Elements(ns + "ImportGroup")
            .FirstOrDefault(g => (string?)g.Attribute("Label") == "ExtensionTargets");
        if (targetsGroup == null)
        {
            targetsGroup = new XElement(ns + "ImportGroup", new XAttribute("Label", "ExtensionTargets"));
            root.Add(targetsGroup);
        }

        bool hasMasmTargets = targetsGroup.Elements(ns + "Import")
            .Any(i => string.Equals((string?)i.Attribute("Project"), masmTargets, StringComparison.OrdinalIgnoreCase));
        if (!hasMasmTargets)
        {
            targetsGroup.Add(new XElement(ns + "Import", new XAttribute("Project", masmTargets)));
        }
    }

    private static XElement GetOrCreate(XElement parent, XName name)
    {
        var child = parent.Element(name);
        if (child is not null) return child;
        child = new XElement(name);
        parent.Add(child);
        return child;
    }

    private static void SetValue(XElement parent, XName name, string value)
    {
        var element = parent.Element(name);
        if (element is null) parent.Add(new XElement(name, value));
        else element.Value = value;
    }
}
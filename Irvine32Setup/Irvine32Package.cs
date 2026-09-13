using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Irvine32Setup;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Irvine32 Setup", "Configures Irvine32 for MASM projects.", "1.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[System.Runtime.InteropServices.Guid(PackageGuidString)]
public sealed class Irvine32Package : AsyncPackage
{
    public const string PackageGuidString = "a4d36b69-0e2d-4a88-9b76-8ccf7d45c7b1";
    private const int ConfigureCommandId = 0x0100;
    private static readonly Guid CommandSet = new("8f5f49c2-3b7c-4d6e-9d5e-4db7b07e5f38");

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService != null)
        {
            var menuCommandId = new CommandID(CommandSet, ConfigureCommandId);
            var menuItem = new OleMenuCommand(ConfigureIrvine32, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        await CheckAndPromptOnSolutionOpenAsync();
    }

    private void ConfigureIrvine32(object sender, EventArgs e)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await ConfigureIrvine32Async(isManualInvocation: true);
        });
    }

    private async Task CheckAndPromptOnSolutionOpenAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        if (dte == null) return;

        var vcxprojPaths = GetVcxprojProjects(dte);
        if (vcxprojPaths.Count == 0) return;

        var unconfigured = vcxprojPaths.Where(p => !IsProjectConfigured(p)).ToList();
        if (unconfigured.Count == 0) return;

        var response = MessageBox.Show(
            "One or more C++ projects in this solution are not yet configured for Irvine32.\n\nWould you like to configure Irvine32 now?",
            "Irvine32 Setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (response == DialogResult.Yes)
        {
            await ConfigureIrvine32Async(isManualInvocation: false);
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

        using var picker = new FolderBrowserDialog
        {
            Description = "Select the folder that contains Irvine32.inc and Irvine32.lib.",
            ShowNewFolderButton = false
        };

        if (picker.ShowDialog() != DialogResult.OK)
            return;

        var includeFile = Path.Combine(picker.SelectedPath, "Irvine32.inc");
        var libraryFile = Path.Combine(picker.SelectedPath, "Irvine32.lib");
        if (!File.Exists(includeFile) || !File.Exists(libraryFile))
        {
            MessageBox.Show(
                "The selected folder must contain both 'Irvine32.inc' and 'Irvine32.lib'.",
                "Irvine32 Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            dte.ExecuteCommand("File.SaveAll");
        }
        catch
        {
            // Ignore if the command is not available in the current IDE state
        }

        var updated = 0;
        foreach (var projectPath in vcxprojPaths)
        {
            try
            {
                ConfigureProject(projectPath, picker.SelectedPath);
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

        MessageBox.Show(
            $"Successfully configured Irvine32 for {updated} project(s).\n\n" +
            "Important Notes:\n" +
            "1. Irvine32 is a 32-bit library. Ensure your build platform is set to 'x86' (Win32).\n" +
            "2. If Visual Studio prompts you to reload the project, select 'Reload'.",
            "Irvine32 Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
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
        catch
        {
            // Ignore items or project kinds that throw on FullName / ProjectItems access
        }
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
        catch
        {
            // Ignore read errors
        }
        return false;
    }

    private static void ConfigureProject(string projectPath, string irvinePath)
    {
        var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root == null) return;
        var ns = root.Name.Namespace;

        // 1. Ensure MASM Build Customization (.props and .targets) are imported
        EnsureMasmBuildCustomizations(document, ns);

        // 2. Configure ItemDefinitionGroup settings
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

    private static void EnsureMasmBuildCustomizations(XDocument document, XNamespace ns)
    {
        var root = document.Root;
        if (root == null) return;

        const string masmProps = @"$(VCTargetsPath)\BuildCustomizations\masm.props";
        const string masmTargets = @"$(VCTargetsPath)\BuildCustomizations\masm.targets";

        // ExtensionSettings
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

        // ExtensionTargets
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

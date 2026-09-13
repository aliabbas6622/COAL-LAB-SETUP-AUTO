using System;
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
        commandService?.AddCommand(new OleMenuCommand(ConfigureIrvine32, new CommandID(CommandSet, ConfigureCommandId)));
        await ConfigureIrvine32Async();
    }

    private async void ConfigureIrvine32(object sender, EventArgs e)
    {
        await ConfigureIrvine32Async();
    }

    private async Task ConfigureIrvine32Async()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();

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
            MessageBox.Show("The selected folder must contain both Irvine32.inc and Irvine32.lib.", "Irvine32 Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        var projects = dte?.Solution.Projects.Cast<EnvDTE.Project>().Where(project => File.Exists(project.FullName)).ToList();
        if (projects is null || projects.Count == 0)
        {
            MessageBox.Show("Open a solution containing a C++ project first.", "Irvine32 Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var updated = 0;
        foreach (var project in projects)
        {
            if (!project.FullName.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
                continue;

            ConfigureProject(project.FullName, picker.SelectedPath);
            updated++;
        }

        dte.ExecuteCommand("File.SaveAll");
        MessageBox.Show($"Configured Irvine32 for {updated} project(s). Reload the solution or rebuild to apply the settings.", "Irvine32 Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void ConfigureProject(string projectPath, string irvinePath)
    {
        var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var ns = document.Root!.Name.Namespace;
        var definitionGroups = document.Root.Elements(ns + "ItemDefinitionGroup").ToList();
        if (definitionGroups.Count == 0)
        {
            var import = document.Root.Elements(ns + "Import").LastOrDefault();
            var group = new XElement(ns + "ItemDefinitionGroup");
            if (import is null) document.Root.Add(group); else import.AddBeforeSelf(group);
            definitionGroups.Add(group);
        }

        foreach (var group in definitionGroups)
        {
            var masm = GetOrCreate(group, ns + "MASM");
            SetValue(masm, ns + "IncludePaths", $"{irvinePath};%(IncludePaths)");

            var condition = (string?)group.Attribute("Condition") ?? string.Empty;
            if (condition.IndexOf("Win32", StringComparison.OrdinalIgnoreCase) >= 0 || condition.Length == 0)
            {
                var link = GetOrCreate(group, ns + "Link");
                SetValue(link, ns + "AdditionalLibraryDirectories", $"{irvinePath};%(AdditionalLibraryDirectories)");
                SetValue(link, ns + "AdditionalDependencies", "Irvine32.lib;%(AdditionalDependencies)");
            }
        }

        document.Save(projectPath);
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
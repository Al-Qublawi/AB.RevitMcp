// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using ABAdvTools.UI;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ABAdvTools.Revit
{
    // These commands must be public: Revit instantiates them by name from the add-in assembly.
    // Every AB add-in carries identical copies; the shared panel points at whichever add-in
    // built it, and all copies behave the same.

    /// <summary>The shared panel buttons work on the Revit home screen, with no model open.</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            return true;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            AdvToolsUi.ShowAbout(CommandOwner.Of(commandData), AdvToolsHost.Revit, false);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class CheckForUpdatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            AdvToolsUi.ShowAbout(CommandOwner.Of(commandData), AdvToolsHost.Revit, true);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class LinkedInCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            AdvToolsUi.OpenLinkedIn(CommandOwner.Of(commandData));
            return Result.Succeeded;
        }
    }

    internal static class CommandOwner
    {
        public static System.Windows.Forms.IWin32Window Of(ExternalCommandData commandData)
        {
            try
            {
                return HostWindow.FromHandle(commandData.Application.MainWindowHandle);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

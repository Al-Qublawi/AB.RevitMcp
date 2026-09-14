// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace ABAdvTools.Setup
{
    /// <summary>The order of operations, shared by the window and silent mode.</summary>
    internal static class SetupEngine
    {
        /// <summary>
        /// Removes the chosen earlier versions, installs, and registers in Apps and Features.
        /// Stops at the first removal that fails: installing beside a copy that could not be
        /// removed is exactly how an add-in ends up loading twice.
        /// </summary>
        public static bool Install(SetupContext ctx, SetupProduct product, InstallPlan plan)
        {
            ctx.ResetProblems();
            ctx.Log(product.Name + " " + product.Version + " - installing for " +
                    (plan.Scope == InstallScope.AllUsers ? "all users" : "the current user"));
            if (ctx.IsSandbox) ctx.Log("SANDBOX: every change goes to " + ctx.SandboxRoot);
            ctx.Log(string.Empty);

            if (plan.Remove.Count > 0)
            {
                ctx.Log("Removing earlier versions...");
                foreach (ExistingInstall old in plan.Remove)
                {
                    ctx.Log(" - " + old.Title + " (" + old.InstalledBy + ", " + old.ScopeText + ")");
                    try
                    {
                        old.Remove(ctx);
                    }
                    catch (Exception ex)
                    {
                        ctx.Log("   FAILED: " + ex.Message);
                        ctx.Log(string.Empty);
                        ctx.Log("Nothing new was installed, so the earlier version keeps working.");
                        return false;
                    }
                }
                ctx.Log(string.Empty);
            }

            try
            {
                ctx.Log("Installing...");
                product.Install(ctx, plan.Scope, plan.Targets);

                var keys = new List<string>();
                foreach (HostTarget target in plan.Targets) keys.Add(target.Key);
                UninstallEntry.Write(ctx, product, plan.Scope, string.Join(";", keys.ToArray()), EstimatedSizeKb());

                product.AfterInstall(ctx, plan);
            }
            catch (Exception ex)
            {
                ctx.Log("   FAILED: " + ex.Message);
                return false;
            }

            ctx.Log(string.Empty);
            if (ctx.ProblemCount > 0)
            {
                ctx.Log(product.Name + " " + product.Version + " is installed, with " + ctx.ProblemCount +
                        " problem(s) - see above.");
                return false;
            }

            ctx.Log(product.Name + " " + product.Version + " is installed.");
            return true;
        }

        /// <summary>Removes the chosen copies. Carries on past a failure so as much as possible goes.</summary>
        public static bool Uninstall(SetupContext ctx, SetupProduct product, IList<ExistingInstall> installs)
        {
            ctx.Log("Removing " + product.Name + "...");
            if (ctx.IsSandbox) ctx.Log("SANDBOX: " + ctx.SandboxRoot);

            bool ok = true;
            foreach (ExistingInstall install in installs)
            {
                ctx.Log(" - " + install.Title + " (" + install.InstalledBy + ", " + install.ScopeText + ")");
                try
                {
                    install.Remove(ctx);
                }
                catch (Exception ex)
                {
                    ctx.Log("   FAILED: " + ex.Message);
                    ok = false;
                }
            }

            if (ok)
            {
                try { product.AfterUninstall(ctx); }
                catch (Exception ex) { ctx.Log("   " + ex.Message); }
            }

            ctx.Log(string.Empty);
            ctx.Log(ok ? product.Name + " has been removed." : "Finished with problems - see above.");
            return ok;
        }

        /// <summary>The first blocking application still running, or null when setup can proceed.</summary>
        public static string RunningHost(SetupContext ctx, SetupProduct product)
        {
            if (ctx.IsSandbox) return null;   // the sandbox touches nothing a running host has open

            foreach (string process in product.BlockingProcesses)
            {
                if (AutodeskLocator.IsRunning(process))
                    return process.Equals(AutodeskLocator.NavisworksProcess, StringComparison.OrdinalIgnoreCase)
                        ? "Navisworks"
                        : process;
            }
            return null;
        }

        /// <summary>Why the plan needs administrator rights, or null when it does not.</summary>
        public static string ElevationReason(SetupContext ctx, SetupProduct product, InstallPlan plan)
        {
            if (ctx.IsSandbox || SetupContext.IsElevated) return null;

            foreach (ExistingInstall old in plan.Remove)
            {
                if (old.NeedsElevation) return "remove " + old.Title + ", which was installed for all users";
            }
            return product.ElevationReason(plan.Scope, plan.Targets);
        }

        public static string ElevationReasonForRemoval(SetupContext ctx, IList<ExistingInstall> installs)
        {
            if (ctx.IsSandbox || SetupContext.IsElevated) return null;

            foreach (ExistingInstall install in installs)
            {
                if (install.NeedsElevation) return "remove " + install.Title + ", which was installed for all users";
            }
            return null;
        }

        private static long EstimatedSizeKb()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                return entry == null ? 1 : new FileInfo(entry.Location).Length / 1024;
            }
            catch
            {
                return 1;
            }
        }
    }
}

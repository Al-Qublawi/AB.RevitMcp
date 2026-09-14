// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ABAdvTools.Setup
{
    /// <summary>An installed Windows Installer (MSI) product.</summary>
    internal sealed class MsiProduct
    {
        public string ProductCode { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public bool PerMachine { get; set; }
    }

    /// <summary>
    /// Finds and removes earlier releases that shipped as an .msi. They are found by UpgradeCode -
    /// constant for the life of a product - rather than by display name, which a release is free
    /// to change.
    /// </summary>
    internal static class WindowsInstaller
    {
        private const int ErrorSuccess = 0;
        private const int ErrorNoMoreItems = 259;
        private const int ErrorMoreData = 234;
        private const int ErrorUnknownProduct = 1605;
        private const int ErrorSuccessRebootRequired = 3010;
        private const int ErrorSuccessRebootInitiated = 1641;
        private const int ErrorInstallUserExit = 1602;
        private const int ErrorInstallAlreadyRunning = 1618;

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiEnumRelatedProducts(string upgradeCode, int reserved, int index, StringBuilder productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiGetProductInfo(string product, string property, StringBuilder value, ref int valueLength);

        public static List<MsiProduct> FindByUpgradeCode(string upgradeCode)
        {
            var found = new List<MsiProduct>();
            if (string.IsNullOrEmpty(upgradeCode)) return found;

            string code = Braced(upgradeCode);
            try
            {
                for (int index = 0; ; index++)
                {
                    var productCode = new StringBuilder(39);
                    int result = MsiEnumRelatedProducts(code, 0, index, productCode);
                    if (result != ErrorSuccess) break;   // ERROR_NO_MORE_ITEMS or a real failure

                    string product = productCode.ToString();
                    found.Add(new MsiProduct
                    {
                        ProductCode = product,
                        Name = Info(product, "InstalledProductName") ?? Info(product, "ProductName"),
                        Version = Info(product, "VersionString"),
                        PerMachine = Info(product, "AssignmentType") == "1"
                    });
                }
            }
            catch (Exception)
            {
                // msi.dll unavailable (never on real Windows) - treat as nothing installed.
            }
            return found;
        }

        /// <summary>
        /// Runs msiexec /x with a basic progress UI and no restart. Throws when Windows Installer
        /// reports a real failure; "product not installed" counts as success.
        /// </summary>
        public static void Uninstall(SetupContext ctx, MsiProduct product)
        {
            ctx.Log("   running Windows Installer to remove " + product.Name + " " + product.Version + "...");

            var info = new ProcessStartInfo("msiexec.exe",
                "/x " + product.ProductCode + " /qb! /norestart REBOOT=ReallySuppress")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(info))
            {
                process.WaitForExit();
                int exit = process.ExitCode;

                switch (exit)
                {
                    case ErrorSuccess:
                    case ErrorUnknownProduct:
                        ctx.Log("   Windows Installer removed it.");
                        return;

                    case ErrorSuccessRebootRequired:
                    case ErrorSuccessRebootInitiated:
                        ctx.Log("   Windows Installer removed it; a restart will finish removing locked files.");
                        return;

                    case ErrorInstallUserExit:
                        throw new InvalidOperationException("Removal of the previous version was cancelled.");

                    case ErrorInstallAlreadyRunning:
                        throw new InvalidOperationException(
                            "Another installation is in progress. Wait for it to finish, then run setup again.");

                    default:
                        throw new InvalidOperationException(
                            "Windows Installer could not remove the previous version (exit code " +
                            exit.ToString(CultureInfo.InvariantCulture) + ").");
                }
            }
        }

        private static string Info(string productCode, string property)
        {
            try
            {
                int length = 256;
                var value = new StringBuilder(length);
                int result = MsiGetProductInfo(productCode, property, value, ref length);
                if (result == ErrorMoreData)
                {
                    length++;
                    value = new StringBuilder(length);
                    result = MsiGetProductInfo(productCode, property, value, ref length);
                }
                return result == ErrorSuccess && value.Length > 0 ? value.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Braced(string guid)
        {
            string trimmed = guid.Trim().Trim('{', '}').ToUpperInvariant();
            return "{" + trimmed + "}";
        }
    }
}

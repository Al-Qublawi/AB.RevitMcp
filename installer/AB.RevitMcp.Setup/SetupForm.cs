using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Windows.Forms;

namespace AB.RevitMcp.Setup
{
    /// <summary>The installer window. Deliberately one screen: choose, install, verify, done.</summary>
    public sealed class SetupForm : Form
    {
        private readonly BundleInstaller _installer;
        private CheckedListBox _revitList;
        private CheckedListBox _agentList;
        private Button _installButton;
        private Button _uninstallButton;
        private Button _verifyButton;
        private TextBox _log;
        private Label _status;
        private LinkLabel _linkedIn;

        public SetupForm()
        {
            _installer = new BundleInstaller(AppendLog);
            BuildUi();
            PopulateRevit();
        }

        private void BuildUi()
        {
            Text = Branding.ProductName + " - Setup";
            Size = new Size(700, 760);
            MinimumSize = new Size(640, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;

            // Window icon, taken from the same .ico the executable uses.
            Icon appIcon = LoadEmbeddedIcon();
            if (appIcon != null) Icon = appIcon;

            var logo = new PictureBox
            {
                Image = LoadEmbeddedImage("AB.RevitMcp.Setup.Resources.logo_96.png"),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(72, 72),
                Location = new Point(20, 18),
                BackColor = Color.Transparent
            };

            const int textLeft = 106;

            var header = new Label
            {
                Text = Branding.ProductName,
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x1F, 0x6F, 0xEB),
                AutoSize = true,
                Location = new Point(textLeft, 18)
            };

            var subtitle = new Label
            {
                Text = "Model Context Protocol bridge for Autodesk Revit 2020-2026.  Version " + Branding.Version,
                AutoSize = true,
                ForeColor = Color.FromArgb(0x50, 0x50, 0x50),
                Location = new Point(textLeft + 2, 50)
            };

            var authorLabel = new Label
            {
                Text = "by " + Branding.Author,
                AutoSize = true,
                ForeColor = Color.FromArgb(0x50, 0x50, 0x50),
                Location = new Point(textLeft + 2, 70)
            };

            _linkedIn = new LinkLabel
            {
                Text = Branding.LinkedInCaption,
                AutoSize = true,
                Location = new Point(textLeft + 2, 90),
                LinkColor = Color.FromArgb(0x0A, 0x66, 0xC2)   // LinkedIn blue
            };
            _linkedIn.LinkClicked += delegate { OpenLinkedIn(); };

            var revitLabel = new Label
            {
                Text = "Revit releases found on this computer:",
                AutoSize = true,
                Location = new Point(20, 126)
            };

            _revitList = new CheckedListBox
            {
                Location = new Point(22, 148),
                Size = new Size(636, 92),
                CheckOnClick = true,
                IntegralHeight = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle
            };

            var agentLabel = new Label
            {
                Text = "AI agents to configure automatically (existing configs are backed up):",
                AutoSize = true,
                Location = new Point(20, 250)
            };

            _agentList = new CheckedListBox
            {
                Location = new Point(22, 272),
                Size = new Size(636, 128),
                CheckOnClick = true,
                IntegralHeight = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle
            };

            var addAgentButton = new Button
            {
                Text = "Add custom agent...",
                Location = new Point(478, 246),
                Size = new Size(180, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            addAgentButton.Click += delegate { AddCustomAgent(); };

            _installButton = new Button
            {
                Text = "Install",
                Location = new Point(22, 412),
                Size = new Size(120, 32),
                BackColor = Color.FromArgb(0x1F, 0x6F, 0xEB),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _installButton.FlatAppearance.BorderSize = 0;
            _installButton.Click += delegate { RunInstall(); };

            _verifyButton = new Button
            {
                Text = "Verify",
                Location = new Point(152, 412),
                Size = new Size(100, 32)
            };
            _verifyButton.Click += delegate { RunVerify(); };

            _uninstallButton = new Button
            {
                Text = "Uninstall",
                Location = new Point(262, 412),
                Size = new Size(100, 32)
            };
            _uninstallButton.Click += delegate { RunUninstall(); };

            _status = new Label
            {
                Text = "Close Revit before installing - add-ins load only at startup.",
                AutoSize = true,
                ForeColor = Color.FromArgb(0xB0, 0x6A, 0x00),
                Location = new Point(22, 454)
            };

            _log = new TextBox
            {
                Location = new Point(22, 478),
                Size = new Size(636, 216),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA),
                Font = new Font("Consolas", 8.5F),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            Controls.AddRange(new Control[]
            {
                logo, header, subtitle, authorLabel, _linkedIn, revitLabel, _revitList,
                agentLabel, addAgentButton, _agentList, _installButton, _verifyButton,
                _uninstallButton, _status, _log
            });
        }

        private void PopulateRevit()
        {
            List<RevitTarget> targets = _installer.DiscoverRevit(null);

            if (targets.Count == 0)
            {
                AppendLog("No Autodesk Revit installation was found on this computer.");
                AppendLog("If Revit lives somewhere other than C:\\Program Files\\Autodesk, install manually.");
                _installButton.Enabled = false;
                return;
            }

            foreach (RevitTarget target in targets)
            {
                _revitList.Items.Add(target, target.PayloadAvailable);
            }

            int supported = targets.Count(t => t.PayloadAvailable);
            AppendLog("Found " + targets.Count + " Revit installation(s); this installer supports " + supported + ".");
            if (supported == 0) _installButton.Enabled = false;

            if (!_installer.HasServerPayload())
            {
                AppendLog("WARNING: no MCP server payload is embedded in this installer.");
                _installButton.Enabled = false;
            }

            PopulateAgents();
        }

        /// <summary>
        /// Lists every AI client we know how to configure, ticking the ones actually present.
        /// Clients that are not installed stay listed but unticked, so it is obvious what the
        /// connector supports rather than silently hiding options.
        /// </summary>
        private void PopulateAgents()
        {
            int detected = 0;
            foreach (AgentTarget agent in AgentConfigurator.KnownAgents())
            {
                _agentList.Items.Add(agent, agent.Detected);
                if (agent.Detected) detected++;
            }
            AppendLog("Detected " + detected + " AI client(s) that can be configured automatically.");
        }

        /// <summary>
        /// Lets the user point at any MCP config file this installer does not know about. The set
        /// of MCP clients changes constantly, and guessing paths would write files to the wrong
        /// place - so for the long tail the user supplies the real one.
        /// </summary>
        private void AddCustomAgent()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the MCP configuration file for your agent";
                dialog.Filter = "MCP config (*.json;*.yaml;*.yml)|*.json;*.yaml;*.yml|All files (*.*)|*.*";
                dialog.CheckFileExists = false;   // the file may not exist yet
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string path = dialog.FileName;
                bool yaml = path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

                string rootKey = "mcpServers";
                if (!yaml)
                {
                    DialogResult answer = MessageBox.Show(this,
                        "Which key does this client use for its server list?" + Environment.NewLine +
                        Environment.NewLine +
                        "Yes  =  \"mcpServers\"   (Claude, Cursor, Windsurf, Cline, LM Studio, most clients)" +
                        Environment.NewLine +
                        "No   =  \"servers\"      (VS Code, Visual Studio)",
                        Branding.ProductName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                    if (answer == DialogResult.Cancel) return;
                    if (answer == DialogResult.No) rootKey = "servers";

                    // Some clients nest the map (OpenClaw uses mcp.servers), so allow a dotted
                    // path. Writing a flat key into a nested config is silently ignored by the
                    // client, which looks exactly like "the connector does not work".
                    string typed = Microsoft.VisualBasic.Interaction.InputBox(
                        "Key holding the server map." + Environment.NewLine +
                        "Use a dotted path if the client nests it, e.g. mcp.servers",
                        Branding.ProductName, rootKey);
                    if (!string.IsNullOrEmpty(typed)) rootKey = typed.Trim();
                }

                AgentTarget custom = AgentConfigurator.Custom(path, rootKey, yaml);
                _agentList.Items.Add(custom, true);
                AppendLog("Added custom agent: " + path + "  (key: " + (yaml ? "YAML list" : rootKey) + ")");
            }
        }

        private IEnumerable<AgentTarget> SelectedAgents()
        {
            foreach (object item in _agentList.CheckedItems)
            {
                var agent = item as AgentTarget;
                if (agent != null) yield return agent;
            }
        }

        private IEnumerable<RevitTarget> SelectedTargets()
        {
            foreach (object item in _revitList.CheckedItems)
            {
                var target = item as RevitTarget;
                if (target != null && target.PayloadAvailable) yield return target;
            }
        }

        private void RunInstall()
        {
            var selected = SelectedTargets().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select at least one Revit release.", Branding.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (Process.GetProcessesByName("Revit").Length > 0)
            {
                DialogResult answer = MessageBox.Show(this,
                    "Revit is currently running.\n\n" +
                    "Its files are locked while it is open, and add-ins only load at startup. " +
                    "Close Revit first for a clean install.\n\nContinue anyway?",
                    Branding.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }

            SetBusy(true);
            AppendLog(string.Empty);
            AppendLog("=== Installing ===");
            try
            {
                bool ok = _installer.Install(selected, SelectedAgents().ToList());
                AppendLog(ok ? "=== Installed successfully ===" : "=== Finished with problems - see above ===");

                if (ok)
                {
                    _status.Text = "Installed. Start Revit, open the \"AB MCP AI\" tab and press Start Bridge.";
                    _status.ForeColor = Color.FromArgb(0x2E, 0xA0, 0x43);
                    AppendLog(string.Empty);
                    AppendLog("Next: start Revit, open the 'AB MCP AI' ribbon tab, press 'Start Bridge',");
                    AppendLog("then restart your AI client so it picks up the new server.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("FAILED: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void RunVerify()
        {
            SetBusy(true);
            AppendLog(string.Empty);
            AppendLog("=== Verifying ===");
            try { AppendLog(_installer.RunDoctor()); }
            catch (Exception ex) { AppendLog("FAILED: " + ex.Message); }
            finally { SetBusy(false); }
        }

        private void RunUninstall()
        {
            DialogResult answer = MessageBox.Show(this,
                "Remove the AB Revit MCP Bridge add-in and server from this computer?",
                Branding.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            SetBusy(true);
            AppendLog(string.Empty);
            AppendLog("=== Uninstalling ===");
            try { _installer.Uninstall(); }
            catch (Exception ex) { AppendLog("FAILED: " + ex.Message); }
            finally { SetBusy(false); }
        }

        private void SetBusy(bool busy)
        {
            _installButton.Enabled = !busy;
            _verifyButton.Enabled = !busy;
            _uninstallButton.Enabled = !busy;
            _revitList.Enabled = !busy;
            _agentList.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            Application.DoEvents();
        }

        private void OpenLinkedIn()
        {
            try { Process.Start(Branding.LinkedInUrl); }
            catch (Exception)
            {
                MessageBox.Show(this, Branding.LinkedInUrl, Branding.LinkedInCaption,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>Loads the product artwork that ships inside this executable.</summary>
        private static Image LoadEmbeddedImage(string resourceName)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;
                    // Image.FromStream keeps the stream alive, so hand it a private copy.
                    var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    buffer.Position = 0;
                    return Image.FromStream(buffer);
                }
            }
            catch (Exception)
            {
                return null;    // artwork is decoration; never let it stop the installer
            }
        }

        private static Icon LoadEmbeddedIcon()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream("AB.RevitMcp.Setup.Resources.logo.ico"))
                {
                    return stream == null ? null : new Icon(stream);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void AppendLog(string text)
        {
            if (text == null) return;
            _log.AppendText(text.TrimEnd() + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            Application.DoEvents();
        }
    }
}

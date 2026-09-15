using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AB.RevitMcp.Addin.AiClients
{
    /// <summary>
    /// AI Clients, on the MCP Bridge panel: point AI clients at the Revit MCP server, add one this
    /// add-in does not know, take the server out again, and Verify the whole chain.
    ///
    /// What AB.RevitMcp.Setup.exe offered on its window up to 1.4.0 - the agent list, "Add custom
    /// agent...", Verify at any time - lives here now that the installer is an .msi that runs no
    /// code. The installer still asks which clients to configure; Revit applies that choice the
    /// first time it starts (AiClientSetup) and shows the result in this window.
    ///
    /// Built at a fixed size with fixed positions: resizing an anchored layout after the fact is
    /// what pushed Setup.exe 1.4.0's "Add custom agent" button off screen.
    /// </summary>
    internal sealed class AiClientsForm : Form
    {
        private readonly CheckedListBox _list;
        private readonly TextBox _log;
        private readonly Label _server;
        private readonly List<Button> _actions = new List<Button>();
        private bool _busy;

        private AiClientsForm()
        {
            Text = "AB Revit MCP Bridge - AI clients";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(640, 540);

            var intro = new Label
            {
                Text = "Point AI clients at the Revit MCP server, so they can reach this model while the bridge is started. " +
                       "Configuration files are backed up first (.abmcp-backup). Restart a client after changing it.",
                Location = new Point(12, 12),
                Size = new Size(616, 36)
            };

            var listLabel = new Label { Text = "AI agents (ticked ones are acted on):", Location = new Point(12, 56), AutoSize = true };

            _list = new CheckedListBox
            {
                Location = new Point(12, 76),
                Size = new Size(616, 184),
                CheckOnClick = true,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle
            };

            Button add = NewAction("Add custom agent...", new Point(12, 270), 150);
            Button configure = NewAction("Configure ticked", new Point(170, 270), 130);
            Button remove = NewAction("Remove from ticked", new Point(308, 270), 140);
            Button verify = NewAction("Verify", new Point(456, 270), 80);
            Button logs = NewAction("Logs", new Point(544, 270), 84);

            _server = new Label { Location = new Point(12, 306), Size = new Size(616, 20), AutoEllipsis = true };

            _log = new TextBox
            {
                Location = new Point(12, 330),
                Size = new Size(616, 160),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9f),
                BackColor = SystemColors.Window
            };

            var close = new Button { Text = "Close", Location = new Point(528, 500), Size = new Size(100, 28), DialogResult = DialogResult.Cancel };
            CancelButton = close;

            add.Click += delegate { AddCustomAgent(); };
            configure.Click += delegate { RunOnTicked("Configuring", delegate (List<AgentTarget> agents, Action<string> log)
            {
                string server = AiClientSetup.ServerPath;
                if (string.IsNullOrEmpty(server) || !File.Exists(server))
                {
                    log("The MCP server was not found (expected at " + server + "). Repair AB Revit MCP Bridge in Apps and Features.");
                    return;
                }
                new AgentConfigurator(log).Configure(agents, server);
            }); };
            remove.Click += delegate { RunOnTicked("Removing the \"revit\" server", delegate (List<AgentTarget> agents, Action<string> log)
            {
                new AgentConfigurator(log).Remove(agents);
            }); };
            verify.Click += delegate { StartVerify(); };
            logs.Click += delegate { OpenFolder(AB.RevitMcp.Contracts.Protocol.IpcConstants.LogDirectory); };

            Controls.AddRange(new Control[] { intro, listLabel, _list, add, configure, remove, verify, logs, _server, _log, close });

            string serverPath = AiClientSetup.ServerPath;
            _server.Text = "Server: " + (File.Exists(serverPath ?? string.Empty) ? serverPath : "not found - expected at " + serverPath);

            // Every client this add-in knows is listed; the ones actually present are ticked, so it
            // is obvious what is supported rather than silently hiding options.
            foreach (AgentTarget agent in AgentConfigurator.KnownAgents())
                _list.Items.Add(agent, agent.Detected);
        }

        private Button NewAction(string text, Point location, int width)
        {
            var button = new Button { Text = text, Location = location, Size = new Size(width, 28) };
            _actions.Add(button);
            return button;
        }

        // ------------------------------------------------------------ entry points

        /// <summary>The AI Clients button. With verifyNow, Verify starts as soon as the window opens.</summary>
        public static void ShowWindow(bool verifyNow)
        {
            using (var form = new AiClientsForm())
            {
                if (verifyNow) form.Shown += delegate { form.StartVerify(); };
                form.ShowDialog(Owner());
            }
        }

        /// <summary>After Revit applied the installer's choice: the same window, with what happened.</summary>
        public static void ShowResults(string headline, IEnumerable<string> lines)
        {
            using (var form = new AiClientsForm())
            {
                form.Append(headline);
                form.Append(string.Empty);
                foreach (string line in lines) form.Append(line);
                form.ShowDialog(Owner());
            }
        }

        private static IWin32Window Owner()
        {
            try { return ABAdvTools.UI.HostWindow.FromHandle(Process.GetCurrentProcess().MainWindowHandle); }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------ actions

        private void StartVerify()
        {
            Run("Verify", delegate (Action<string> log) { AiClientSetup.Verify(log); });
        }

        private void RunOnTicked(string what, Action<List<AgentTarget>, Action<string>> work)
        {
            var agents = new List<AgentTarget>();
            foreach (object item in _list.CheckedItems)
            {
                var agent = item as AgentTarget;
                if (agent != null) agents.Add(agent);
            }

            if (agents.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one AI agent first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Run(what, delegate (Action<string> log) { work(agents, log); });
        }

        /// <summary>Runs work off Revit's UI thread - the launch probe starts processes - with its log shown here.</summary>
        private void Run(string what, Action<Action<string>> work)
        {
            if (_busy) return;
            _busy = true;
            SetButtons(false);
            Append(string.Empty);
            Append("=== " + what + " ===");

            Action<string> log = delegate (string line) { PostLine(line); };

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(log); }
                catch (Exception ex) { log("FAILED: " + ex.Message); }
                finally
                {
                    Post(delegate
                    {
                        _busy = false;
                        SetButtons(true);
                        RefreshList();
                        Append("Done.");
                    });
                }
            });
        }

        private void AddCustomAgent()
        {
            using (var dialog = new CustomAgentDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                AgentTarget agent = AgentConfigurator.Custom(dialog.ConfigPath, dialog.RootKey, AgentConfigurator.IsYamlPath(dialog.ConfigPath));
                _list.Items.Add(agent, true);
                Append("Added " + agent.Name + " (" + agent.ConfigPath + ", key " + agent.RootKey + "). Press Configure ticked to write it.");
            }
        }

        // ------------------------------------------------------------ plumbing

        private void RefreshList()
        {
            // CheckedListBox caches item text; re-setting each item re-reads "(configured)".
            for (int i = 0; i < _list.Items.Count; i++)
            {
                bool ticked = _list.GetItemChecked(i);
                _list.Items[i] = _list.Items[i];
                _list.SetItemChecked(i, ticked);
            }
        }

        private void SetButtons(bool enabled)
        {
            foreach (Button button in _actions) button.Enabled = enabled;
            UseWaitCursor = !enabled;
        }

        private void Append(string line)
        {
            _log.AppendText((line ?? string.Empty) + Environment.NewLine);
        }

        private void PostLine(string line)
        {
            Post(delegate { Append(line); });
        }

        private void Post(Action action)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (Exception) { }   // the window closed while work was still running
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Points at an MCP config file this add-in does not know. The set of MCP clients changes
    /// constantly, and guessing paths would write files to the wrong place.
    /// </summary>
    internal sealed class CustomAgentDialog : Form
    {
        private readonly TextBox _path;
        private readonly ComboBox _key;

        public CustomAgentDialog()
        {
            Text = "Add custom agent";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(520, 250);

            var pathLabel = new Label { Text = "The agent's MCP configuration file (it may not exist yet):", Location = new Point(12, 14), AutoSize = true };
            _path = new TextBox { Location = new Point(12, 36), Size = new Size(400, 24) };
            var browse = new Button { Text = "Browse...", Location = new Point(420, 34), Size = new Size(88, 28) };

            var keyLabel = new Label { Text = "Key holding its server list:", Location = new Point(12, 76), AutoSize = true };
            _key = new ComboBox { Location = new Point(12, 98), Size = new Size(200, 24), DropDownStyle = ComboBoxStyle.DropDown };
            _key.Items.AddRange(new object[] { "mcpServers", "servers", "mcp.servers" });
            _key.Text = "mcpServers";

            var hint = new Label
            {
                Text = "mcpServers - Claude, Cursor, Windsurf, Cline, LM Studio and most clients\r\n" +
                       "servers - VS Code, Visual Studio\r\n" +
                       "a dotted path such as mcp.servers when the client nests the map (OpenClaw).\r\n" +
                       "A .yaml or .yml file is treated as Continue-style YAML.",
                Location = new Point(12, 130),
                Size = new Size(496, 72)
            };

            var ok = new Button { Text = "Add", Location = new Point(316, 212), Size = new Size(92, 28), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Location = new Point(416, 212), Size = new Size(92, 28), DialogResult = DialogResult.Cancel };
            AcceptButton = ok;
            CancelButton = cancel;

            browse.Click += delegate
            {
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "Select the MCP configuration file for your agent";
                    dialog.Filter = "MCP config (*.json;*.yaml;*.yml)|*.json;*.yaml;*.yml|All files (*.*)|*.*";
                    dialog.CheckFileExists = false;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.FileName;
                }
            };

            ok.Click += delegate (object sender, EventArgs e)
            {
                if (string.IsNullOrWhiteSpace(_path.Text))
                {
                    MessageBox.Show(this, "Choose the agent's configuration file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    DialogResult = DialogResult.None;
                }
            };

            Controls.AddRange(new Control[] { pathLabel, _path, browse, keyLabel, _key, hint, ok, cancel });
        }

        public string ConfigPath { get { return _path.Text.Trim().Trim('"'); } }

        public string RootKey { get { return string.IsNullOrWhiteSpace(_key.Text) ? "mcpServers" : _key.Text.Trim(); } }
    }
}

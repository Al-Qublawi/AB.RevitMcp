// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ABAdvTools.UI;

namespace ABAdvTools.Setup
{
    /// <summary>
    /// The setup window, shared by every AB add-in.
    ///
    ///   Install:    Welcome  ->  Terms (when the product has them)  ->  Earlier versions (when found)  ->  Progress
    ///   Uninstall:  Remove   ->  Progress
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private enum Page { Welcome, License, Existing, Uninstall, Progress }

        /// <summary>Width available to page content, and to a product's options control.</summary>
        internal const int ContentWidth = 612;

        /// <summary>A product's terms, shown before installing when its setup embeds one.</summary>
        private const string LicenseResource = "ABAdvTools.Assets.license.rtf";

        private readonly SetupProduct _product;
        private readonly SetupArguments _args;
        private readonly SetupContext _ctx;

        private List<HostTarget> _targets = new List<HostTarget>();
        private List<ExistingInstall> _existing = new List<ExistingInstall>();

        private Page _page;
        private bool _uninstallMode;
        private bool _working;
        private bool _finished;
        private bool _toolRun;

        // chrome
        private Panel _content;
        private Button _back;
        private Button _next;
        private Button _cancel;
        private LinkLabel _modeLink;

        // pages
        private Panel _welcomePage;
        private CheckedListBox _targetList;
        private RadioButton _currentUser;
        private RadioButton _allUsers;
        private Label _welcomeNote;

        private Panel _licensePage;
        private CheckBox _licenseAccepted;

        private Panel _existingPage;
        private ListView _existingList;
        private Label _existingWarning;

        private Panel _uninstallPage;
        private ListView _uninstallList;

        private Panel _progressPage;
        private Label _progressStatus;
        private ProgressBar _progressBar;
        private TextBox _log;
        private FlowLayoutPanel _finishActions;

        private Image _logo;

        public SetupForm(SetupProduct product, SetupArguments args)
        {
            _product = product;
            _args = args ?? new SetupArguments();
            _ctx = new SetupContext(AppendLogThreadSafe, _args.Sandbox, _args.UserAppData, _args.UserLocalAppData);

            BuildChrome();
            BuildWelcomePage();
            BuildLicensePage();
            BuildExistingPage();
            BuildUninstallPage();
            BuildProgressPage();

            Discover();

            _uninstallMode = _args.Uninstall;
            ShowPage(_uninstallMode ? Page.Uninstall : Page.Welcome);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_args.AutoRun) BeginInvoke((MethodInvoker)Proceed);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_working && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;   // never abandon a half-finished install
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _logo != null) { _logo.Dispose(); _logo = null; }
            base.Dispose(disposing);
        }

        // ============================================================ layout

        private void BuildChrome()
        {
            Text = _product.Name + " " + _product.Version + " Setup" + (_ctx.IsSandbox ? "  [SANDBOX]" : string.Empty);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(660, 640);
            BackColor = Color.White;

            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location); }
            catch { }

            var banner = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = AdvToolsUi.BannerBack };

            _logo = AdvToolsUi.LoadImage("product_logo.png");
            int textLeft = 20;
            if (_logo != null)
            {
                banner.Controls.Add(new PictureBox
                {
                    Image = _logo,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Location = new Point(18, 14),
                    Size = new Size(72, 72),
                    BackColor = Color.Transparent
                });
                textLeft = 104;
            }

            banner.Controls.Add(new Label
            {
                Text = _product.Name,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(textLeft, 14),
                BackColor = Color.Transparent
            });
            banner.Controls.Add(new Label
            {
                Text = "Version " + _product.Version + "   |   " + _product.HostSummary,
                ForeColor = AdvToolsUi.BannerSubtitle,
                AutoSize = true,
                Location = new Point(textLeft + 2, 50),
                BackColor = Color.Transparent
            });

            var author = new LinkLabel
            {
                Text = AdvToolsBrand.SuiteName + "  -  by " + AdvToolsBrand.Author,
                AutoSize = true,
                Location = new Point(textLeft + 2, 72),
                BackColor = Color.Transparent,
                LinkColor = AdvToolsUi.BannerSubtitle,
                ActiveLinkColor = Color.White,
                VisitedLinkColor = AdvToolsUi.BannerSubtitle,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            author.LinkArea = new LinkArea(author.Text.IndexOf(AdvToolsBrand.Author, StringComparison.Ordinal), AdvToolsBrand.Author.Length);
            author.LinkClicked += delegate { AdvToolsBrand.OpenUrl(AdvToolsBrand.LinkedInUrl); };
            banner.Controls.Add(author);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(245, 246, 248) };
            footer.Paint += delegate (object s, PaintEventArgs pe)
            {
                pe.Graphics.DrawLine(SystemPens.ControlLight, 0, 0, footer.Width, 0);
            };

            _cancel = new Button { Text = "Cancel", Size = new Size(96, 30), Location = new Point(546, 13) };
            _next = new Button { Text = "Next >", Size = new Size(110, 30), Location = new Point(428, 13) };
            _back = new Button { Text = "< Back", Size = new Size(96, 30), Location = new Point(324, 13) };
            _cancel.Click += delegate { Close(); };
            _next.Click += delegate { Proceed(); };
            _back.Click += delegate { GoBack(); };

            _modeLink = new LinkLabel
            {
                AutoSize = true,
                Location = new Point(18, 20),
                LinkBehavior = LinkBehavior.HoverUnderline,
                LinkColor = AdvToolsUi.LinkBlue
            };
            _modeLink.LinkClicked += delegate { ToggleMode(); };

            footer.Controls.AddRange(new Control[] { _modeLink, _back, _next, _cancel });

            _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 10) };

            Controls.Add(_content);
            Controls.Add(footer);
            Controls.Add(banner);
            AcceptButton = _next;
        }

        private Panel NewPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, Visible = false };
            _content.Controls.Add(page);
            return page;
        }

        private static Label Heading(string text, int top)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 30),
                AutoSize = true,
                Location = new Point(0, top)
            };
        }

        private void BuildWelcomePage()
        {
            _welcomePage = NewPage();

            var intro = new Label
            {
                Text = _product.Description,
                Location = new Point(0, 0),
                Size = new Size(612, 40)
            };

            var targetsLabel = new Label { Text = "Install into:", AutoSize = true, Location = new Point(0, 48) };

            _targetList = new CheckedListBox
            {
                Location = new Point(2, 70),
                Size = new Size(612, 112),
                CheckOnClick = true,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle
            };
            _targetList.ItemCheck += delegate (object s, ItemCheckEventArgs e)
            {
                // Releases this installer has no build for stay unticked.
                var target = _targetList.Items[e.Index] as HostTarget;
                if (target != null && !target.PayloadAvailable) e.NewValue = CheckState.Unchecked;
            };

            _currentUser = new RadioButton
            {
                Text = "Only me  (no administrator rights needed)",
                AutoSize = true,
                Location = new Point(14, 22)
            };
            _allUsers = new RadioButton
            {
                Text = "Everyone who uses this computer  (needs administrator rights)",
                AutoSize = true,
                Location = new Point(14, 46)
            };

            InstallScope scope = _args.Scope ?? _product.DefaultScope;
            if (!_product.ScopeSelectable) scope = _product.DefaultScope;
            _allUsers.Checked = scope == InstallScope.AllUsers;
            _currentUser.Checked = scope == InstallScope.CurrentUser;

            int top;
            if (_product.ScopeSelectable)
            {
                var scopeBox = new GroupBox
                {
                    Text = "Install for",
                    Location = new Point(2, 192),
                    Size = new Size(612, 78)
                };
                scopeBox.Controls.Add(_currentUser);
                scopeBox.Controls.Add(_allUsers);
                _welcomePage.Controls.Add(scopeBox);
                top = 280;
            }
            else
            {
                // Nothing to choose: say where it goes, in one line, and give the room to options.
                _welcomePage.Controls.Add(new Label
                {
                    Text = scope == InstallScope.AllUsers
                        ? "Installs for everyone who uses this computer."
                        : "Installs for the current Windows user - no administrator rights needed.",
                    AutoSize = true,
                    ForeColor = Color.FromArgb(90, 90, 90),
                    Location = new Point(0, 190)
                });
                top = 216;
            }
            // Built at its final width by the product; resizing it here would move anchored children.
            Control options = _product.CreateOptionsControl(ContentWidth);
            if (options != null)
            {
                options.Location = new Point(2, top);
                _welcomePage.Controls.Add(options);
                top += options.Height + 8;
            }

            // Things the product can do without installing, e.g. verify what is installed now.
            IList<SetupAction> tools = _product.ToolActions;
            if (tools.Count > 0)
            {
                var row = new FlowLayoutPanel
                {
                    Location = new Point(0, top),
                    Size = new Size(ContentWidth, 34),
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false
                };
                foreach (SetupAction tool in tools)
                {
                    SetupAction captured = tool;
                    var button = new Button { Text = tool.Text, AutoSize = true, Height = 28 };
                    button.Click += delegate { RunToolAction(captured); };
                    row.Controls.Add(button);
                }
                _welcomePage.Controls.Add(row);
                top += 38;
            }

            _welcomeNote = new Label
            {
                Location = new Point(0, top),
                Size = new Size(612, 40),
                ForeColor = Color.FromArgb(176, 106, 0)
            };

            _welcomePage.Controls.AddRange(new Control[] { intro, targetsLabel, _targetList, _welcomeNote });

            // A product with a tall options panel still fits; the page scrolls rather than clips.
            _welcomePage.AutoScroll = true;
        }

        /// <summary>
        /// The product's terms and read-me, when its setup embeds license.rtf - the page the earlier
        /// MSI installers showed. Installing needs the box ticked; silent installs imply acceptance,
        /// as msiexec /qn did.
        /// </summary>
        private void BuildLicensePage()
        {
            string rtf = LicenseRtf();
            if (rtf == null) return;

            _licensePage = NewPage();

            var text = new RichTextBox
            {
                Location = new Point(2, 34),
                Size = new Size(ContentWidth, 330),
                ReadOnly = true,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                DetectUrls = true
            };
            try { text.Rtf = rtf; }
            catch { text.Text = rtf; }
            text.LinkClicked += delegate (object s, LinkClickedEventArgs e) { AdvToolsBrand.OpenUrl(e.LinkText); };

            _licenseAccepted = new CheckBox
            {
                Text = "I have read this and accept the terms",
                AutoSize = true,
                Location = new Point(2, 374)
            };
            _licenseAccepted.CheckedChanged += delegate { if (_page == Page.License) _next.Enabled = _licenseAccepted.Checked; };

            _licensePage.Controls.AddRange(new Control[] { Heading("Before you install", 0), text, _licenseAccepted });
        }

        private static string LicenseRtf()
        {
            try
            {
                using (System.IO.Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LicenseResource))
                {
                    if (stream == null) return null;
                    using (var reader = new System.IO.StreamReader(stream)) return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private void BuildExistingPage()
        {
            _existingPage = NewPage();

            var explanation = new Label
            {
                Text = "These copies of " + _product.Name + " are already on this computer. Removing them " +
                       "before installing is recommended: two copies would load the add-in twice, and an " +
                       "earlier installer could later delete files of the new version.",
                Location = new Point(0, 34),
                Size = new Size(612, 50)
            };

            _existingList = NewInstallList(new Point(2, 90), new Size(612, 190));

            var hint = new Label
            {
                Text = "Ticked copies are removed first. Settings and logs are kept.",
                AutoSize = true,
                ForeColor = Color.FromArgb(90, 90, 90),
                Location = new Point(0, 288)
            };

            _existingWarning = new Label
            {
                Location = new Point(0, 312),
                Size = new Size(612, 60),
                ForeColor = Color.FromArgb(176, 106, 0)
            };

            _existingPage.Controls.AddRange(new Control[]
            {
                Heading("Earlier versions found", 0), explanation, _existingList, hint, _existingWarning
            });
        }

        private void BuildUninstallPage()
        {
            _uninstallPage = NewPage();

            var explanation = new Label
            {
                Text = "Tick the copies of " + _product.Name + " to remove from this computer. " +
                       "Settings and logs are kept.",
                Location = new Point(0, 34),
                Size = new Size(612, 40)
            };

            _uninstallList = NewInstallList(new Point(2, 80), new Size(612, 250));

            _uninstallPage.Controls.AddRange(new Control[]
            {
                Heading("Remove " + _product.Name, 0), explanation, _uninstallList
            });
        }

        private static ListView NewInstallList(Point location, Size size)
        {
            var list = new ListView
            {
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Location = location,
                Size = size,
                BorderStyle = BorderStyle.FixedSingle
            };
            list.Columns.Add("Found", 150);
            list.Columns.Add("Installed by", 170);
            list.Columns.Add("For", 90);
            list.Columns.Add("Where", 380);
            return list;
        }

        private void BuildProgressPage()
        {
            _progressPage = NewPage();

            _progressStatus = new Label
            {
                Text = "Working...",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(0, 0)
            };

            _progressBar = new ProgressBar
            {
                Location = new Point(2, 32),
                Size = new Size(612, 14),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30
            };

            _log = new TextBox
            {
                Location = new Point(2, 56),
                Size = new Size(612, 250),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 8.5F)
            };

            _finishActions = new FlowLayoutPanel
            {
                Location = new Point(0, 314),
                Size = new Size(614, 40),
                FlowDirection = FlowDirection.LeftToRight
            };

            _progressPage.Controls.AddRange(new Control[] { _progressStatus, _progressBar, _log, _finishActions });
        }

        // ============================================================ discovery

        private void Discover()
        {
            try { _targets = _product.FindTargets(_ctx); }
            catch (Exception ex) { _targets = new List<HostTarget>(); ShowError("Could not look for installed releases: " + ex.Message); }

            try { _existing = _product.FindExistingInstalls(_ctx); }
            catch (Exception ex) { _existing = new List<ExistingInstall>(); ShowError("Could not look for earlier versions: " + ex.Message); }

            _targetList.Items.Clear();
            foreach (HostTarget target in _targets)
            {
                bool check = target.PayloadAvailable &&
                             (_args.Targets == null || SetupProgram.Contains(_args.Targets, target.Key));
                _targetList.Items.Add(target, check);
            }

            int supported = 0;
            foreach (HostTarget target in _targets) if (target.PayloadAvailable) supported++;

            string note;
            if (_targets.Count == 0)
                note = "No supported Autodesk release was found on this computer, so there is nothing to install into.";
            else if (supported == 0)
                note = "This installer has no build for the releases found on this computer.";
            else
                note = "Close " + string.Join(" and ", HostNames()) + " before installing: add-ins load only at startup, " +
                       "and a running application keeps the files locked.";
            if (_ctx.IsSandbox) note = "SANDBOX MODE - every change goes to " + _ctx.SandboxRoot;
            _welcomeNote.Text = note;

            FillInstallList(_existingList, _existing, delegate (ExistingInstall install)
            {
                return _args.Remove == null || SetupProgram.Contains(_args.Remove, install.Key);
            });
            FillInstallList(_uninstallList, _existing, delegate (ExistingInstall install)
            {
                return _args.Remove == null || SetupProgram.Contains(_args.Remove, install.Key);
            });

            _existingWarning.Text = NewerVersionWarning();
        }

        private static void FillInstallList(ListView list, List<ExistingInstall> installs, Predicate<ExistingInstall> check)
        {
            list.Items.Clear();
            foreach (ExistingInstall install in installs)
            {
                var item = new ListViewItem(install.Title) { Tag = install, Checked = check(install) };
                item.SubItems.Add(install.InstalledBy);
                item.SubItems.Add(install.ScopeText);
                item.SubItems.Add(string.Join("; ", install.Locations.ToArray()));
                item.ToolTipText = string.Join(Environment.NewLine, install.Locations.ToArray());
                list.Items.Add(item);
            }
            list.ShowItemToolTips = true;
        }

        private string NewerVersionWarning()
        {
            foreach (ExistingInstall install in _existing)
            {
                if (!string.IsNullOrEmpty(install.Version) && UpdateChecker.Compare(install.Version, _product.Version) > 0)
                {
                    return "Version " + install.Version + " is newer than this installer (" + _product.Version + "). " +
                           "Continuing replaces it with the older version.";
                }
            }
            return string.Empty;
        }

        private IEnumerable<string> HostNames()
        {
            var names = new List<string>();
            foreach (string process in _product.BlockingProcesses)
            {
                if (process.Equals(AutodeskLocator.NavisworksProcess, StringComparison.OrdinalIgnoreCase)) names.Add("Navisworks");
                else if (process.Equals(AutodeskLocator.RevitProcess, StringComparison.OrdinalIgnoreCase)) names.Add("Revit");
            }
            return names.Count == 0 ? new[] { "the Autodesk application" } : (IEnumerable<string>)names;
        }

        // ============================================================ navigation

        private void ShowPage(Page page)
        {
            _page = page;
            _welcomePage.Visible = page == Page.Welcome;
            if (_licensePage != null) _licensePage.Visible = page == Page.License;
            _existingPage.Visible = page == Page.Existing;
            _uninstallPage.Visible = page == Page.Uninstall;
            _progressPage.Visible = page == Page.Progress;

            // A finished tool action (e.g. Verify) goes back to the welcome page rather than closing.
            _back.Visible = page == Page.License || page == Page.Existing || (page == Page.Progress && _toolRun && _finished);
            _cancel.Visible = page != Page.Progress || !_finished;
            _cancel.Enabled = page != Page.Progress;

            switch (page)
            {
                case Page.Welcome:
                    _next.Text = _existing.Count > 0 || _licensePage != null ? "Next >" : "Install";
                    _next.Enabled = SupportedCount() > 0;
                    break;
                case Page.License:
                    _next.Text = _existing.Count > 0 ? "Next >" : "Install";
                    _next.Enabled = _licenseAccepted.Checked;
                    break;
                case Page.Existing:
                    _next.Text = "Install";
                    _next.Enabled = true;
                    break;
                case Page.Uninstall:
                    _next.Text = "Remove";
                    _next.Enabled = _existing.Count > 0;
                    break;
                case Page.Progress:
                    _next.Text = _toolRun ? "Close" : "Finish";
                    _next.Enabled = _finished;
                    break;
            }

            bool canToggle = (page == Page.Welcome && _existing.Count > 0) || (page == Page.Uninstall && SupportedCount() > 0);
            _modeLink.Visible = canToggle;
            _modeLink.Text = page == Page.Uninstall ? "Install instead" : "Uninstall " + _product.Name + "...";

            if (page == Page.Uninstall && _existing.Count == 0)
            {
                _uninstallList.Items.Clear();
                _uninstallList.Items.Add(new ListViewItem(_product.Name + " is not installed on this computer."));
            }
        }

        private void GoBack()
        {
            if (_working) return;

            switch (_page)
            {
                case Page.Existing:
                    ShowPage(_licensePage != null ? Page.License : Page.Welcome);
                    break;

                case Page.Progress:
                    // Only a finished tool action offers Back. What it did may have changed what is
                    // installed, so look again.
                    _toolRun = false;
                    _finished = false;
                    Discover();
                    ShowPage(_uninstallMode ? Page.Uninstall : Page.Welcome);
                    break;

                default:
                    ShowPage(_uninstallMode ? Page.Uninstall : Page.Welcome);
                    break;
            }
        }

        private void ToggleMode()
        {
            _uninstallMode = _page != Page.Uninstall;
            ShowPage(_uninstallMode ? Page.Uninstall : Page.Welcome);
        }

        private void Proceed()
        {
            if (_working) return;

            switch (_page)
            {
                case Page.Welcome:
                    if (CheckedTargets().Count == 0)
                    {
                        MessageBox.Show(this, "Tick at least one release to install into.", Text,
                                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    if (_args.AutoRun) StartInstall();            // choices were made before the restart
                    else if (_licensePage != null) ShowPage(Page.License);
                    else if (_existing.Count > 0) ShowPage(Page.Existing);
                    else StartInstall();
                    break;

                case Page.License:
                    if (!_licenseAccepted.Checked) return;
                    if (_existing.Count > 0) ShowPage(Page.Existing);
                    else StartInstall();
                    break;

                case Page.Existing:
                    StartInstall();
                    break;

                case Page.Uninstall:
                    StartUninstall();
                    break;

                case Page.Progress:
                    if (_finished) Close();
                    break;
            }
        }

        // ============================================================ work

        private void StartInstall()
        {
            var plan = new InstallPlan { Scope = _allUsers.Checked ? InstallScope.AllUsers : InstallScope.CurrentUser };
            if (!_product.ScopeSelectable) plan.Scope = _product.DefaultScope;
            plan.Targets.AddRange(CheckedTargets());

            if (_existing.Count > 0)
            {
                foreach (ListViewItem item in _existingList.Items)
                {
                    var install = item.Tag as ExistingInstall;
                    if (install != null && item.Checked) plan.Remove.Add(install);
                }

                if (plan.Remove.Count < _existing.Count && !_args.AutoRun)
                {
                    DialogResult keep = MessageBox.Show(this,
                        "Some earlier copies will be left in place." + Environment.NewLine + Environment.NewLine +
                        "Revit and Navisworks may then load " + _product.Name + " twice, and the earlier " +
                        "copy's uninstaller could later remove files the new version needs." + Environment.NewLine +
                        Environment.NewLine + "Install anyway?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (keep != DialogResult.Yes) return;
                }
            }

            string reason = SetupEngine.ElevationReason(_ctx, _product, plan);
            if (reason != null)
            {
                var targetKeys = new List<string>();
                foreach (HostTarget target in plan.Targets) targetKeys.Add(target.Key);
                var removeKeys = new List<string>();
                foreach (ExistingInstall old in plan.Remove) removeKeys.Add(old.Key);

                RestartElevated(reason, SetupArguments.ForElevatedRestart(false, plan.Scope, targetKeys, removeKeys, _ctx, _args.LogFile));
                return;
            }

            if (!HostsClosed()) return;

            _product.CaptureOptions();
            RunWork(delegate { return SetupEngine.Install(_ctx, _product, plan); }, true);
        }

        private void StartUninstall()
        {
            var installs = new List<ExistingInstall>();
            foreach (ListViewItem item in _uninstallList.Items)
            {
                var install = item.Tag as ExistingInstall;
                if (install != null && item.Checked) installs.Add(install);
            }

            if (installs.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one copy to remove.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string reason = SetupEngine.ElevationReasonForRemoval(_ctx, installs);
            if (reason != null)
            {
                var removeKeys = new List<string>();
                foreach (ExistingInstall install in installs) removeKeys.Add(install.Key);
                RestartElevated(reason, SetupArguments.ForElevatedRestart(true, _product.DefaultScope, null, removeKeys, _ctx, _args.LogFile));
                return;
            }

            if (!HostsClosed()) return;

            RunWork(delegate { return SetupEngine.Uninstall(_ctx, _product, installs); }, false);
        }

        private bool HostsClosed()
        {
            while (true)
            {
                string running = SetupEngine.RunningHost(_ctx, _product);
                if (running == null) return true;

                if (_product.AllowInstallWhileHostRunning)
                {
                    // As this add-in's earlier installer did: warn, and let the user decide. Any
                    // release whose files are locked is skipped and reported, the rest install.
                    DialogResult carryOn = MessageBox.Show(this,
                        running + " is currently running." + Environment.NewLine + Environment.NewLine +
                        "Its files are locked while it is open, and add-ins only load at startup. " +
                        "Close " + running + " first for a clean install." + Environment.NewLine + Environment.NewLine +
                        "Continue anyway?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    return carryOn == DialogResult.Yes;
                }

                DialogResult answer = MessageBox.Show(this,
                    running + " is running." + Environment.NewLine + Environment.NewLine +
                    "Save your work and close " + running + ", then click Retry. It keeps the add-in's files " +
                    "locked, and add-ins only load when it starts.",
                    Text, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (answer != DialogResult.Retry) return false;
            }
        }

        private void RestartElevated(string reason, string arguments)
        {
            DialogResult answer = _args.AutoRun
                ? DialogResult.OK
                : MessageBox.Show(this,
                    "Administrator rights are needed to " + reason + "." + Environment.NewLine + Environment.NewLine +
                    "Windows will ask for permission, then setup carries on with the same choices.",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (answer != DialogResult.OK) return;

            try
            {
                var info = new ProcessStartInfo(Assembly.GetEntryAssembly().Location, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(info);
                Close();
            }
            catch (Win32Exception ex)
            {
                // 1223: the user said no at the Windows prompt.
                if (ex.NativeErrorCode != 1223) ShowError("Could not restart setup as administrator: " + ex.Message);
            }
        }

        /// <summary>Runs a product tool (e.g. Verify) with the log visible; Back returns to the welcome page.</summary>
        private void RunToolAction(SetupAction action)
        {
            if (_working) return;
            _toolRun = true;
            RunWork(delegate
            {
                action.Run(_ctx);
                return true;
            }, false, action.Text + "...");
        }

        private void RunWork(Func<bool> work, bool installing)
        {
            _toolRun = false;
            RunWork(work, installing, installing ? "Installing " + _product.Name + "..." : "Removing " + _product.Name + "...");
        }

        private void RunWork(Func<bool> work, bool installing, string title)
        {

            _working = true;
            _finished = false;
            _log.Clear();
            _finishActions.Controls.Clear();
            _progressStatus.Text = title;
            _progressStatus.ForeColor = Color.FromArgb(30, 30, 30);
            _progressBar.Style = ProgressBarStyle.Marquee;
            ShowPage(Page.Progress);
            _modeLink.Visible = false;

            var thread = new Thread(delegate ()
            {
                bool ok;
                try { ok = work(); }
                catch (Exception ex) { AppendLogThreadSafe("FAILED: " + ex.Message); ok = false; }

                BeginInvoke((MethodInvoker)delegate { WorkFinished(ok, installing); });
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void WorkFinished(bool ok, bool installing)
        {
            _working = false;
            _finished = true;

            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = _progressBar.Maximum;

            if (_toolRun)
            {
                _progressStatus.Text = ok ? "Done" : "Finished with problems - see the log";
                _progressStatus.ForeColor = ok ? AdvToolsUi.UpdateGreen : Color.FromArgb(200, 40, 40);
                ShowPage(Page.Progress);
                _cancel.Visible = false;
                return;
            }

            if (ok)
            {
                _progressStatus.Text = installing ? _product.Name + " is installed" : _product.Name + " has been removed";
                _progressStatus.ForeColor = AdvToolsUi.UpdateGreen;
                if (installing)
                {
                    AppendLog(string.Empty);
                    AppendLog("Next: " + _product.NextSteps);
                    foreach (SetupAction action in _product.FinishActions) AddFinishAction(action);
                }
            }
            else
            {
                _progressStatus.Text = "Finished with problems - see the log";
                _progressStatus.ForeColor = Color.FromArgb(200, 40, 40);
            }

            var release = new LinkLabel
            {
                Text = "Release notes on GitHub",
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 3),
                LinkColor = AdvToolsUi.LinkBlue,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            release.LinkClicked += delegate { AdvToolsBrand.OpenUrl(_product.RepositoryUrl + "/releases"); };
            _finishActions.Controls.Add(release);

            ShowPage(Page.Progress);
            _cancel.Visible = false;
        }

        private void AddFinishAction(SetupAction action)
        {
            var button = new Button { Text = action.Text, AutoSize = true, Height = 30 };
            button.Click += delegate
            {
                button.Enabled = false;
                var thread = new Thread(delegate ()
                {
                    try { action.Run(_ctx); }
                    catch (Exception ex) { AppendLogThreadSafe("FAILED: " + ex.Message); }
                    BeginInvoke((MethodInvoker)delegate { button.Enabled = true; });
                });
                thread.IsBackground = true;
                thread.Start();
            };
            _finishActions.Controls.Add(button);
        }

        // ============================================================ helpers

        private List<HostTarget> CheckedTargets()
        {
            var list = new List<HostTarget>();
            foreach (object item in _targetList.CheckedItems)
            {
                var target = item as HostTarget;
                if (target != null && target.PayloadAvailable) list.Add(target);
            }
            return list;
        }

        private int SupportedCount()
        {
            int count = 0;
            foreach (HostTarget target in _targets) if (target.PayloadAvailable) count++;
            return count;
        }

        private void AppendLogThreadSafe(string line)
        {
            if (_log == null || _log.IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { AppendLog(line); }); }
                catch (InvalidOperationException) { }
                return;
            }
            AppendLog(line);
        }

        private void AppendLog(string line)
        {
            _log.AppendText((line ?? string.Empty) + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ============================================================ QA rendering

        /// <summary>
        /// /render:&lt;folder&gt; - draws every page of this product's setup to PNG files, off screen, and
        /// exits. Detection runs for real but read-only, so the pictures show what a user on this
        /// machine would see. Nothing is installed or removed.
        /// </summary>
        internal static int RenderPages(SetupProduct product, SetupArguments args)
        {
            System.IO.Directory.CreateDirectory(args.RenderFolder);

            using (var form = new SetupForm(product, args))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000);
                form.ShowInTaskbar = false;
                form.Show();
                Application.DoEvents();

                var pages = new List<Page> { Page.Welcome };
                if (form._licensePage != null) pages.Add(Page.License);
                pages.Add(Page.Existing);
                pages.Add(Page.Uninstall);

                foreach (Page page in pages)
                {
                    form.ShowPage(page);
                    Application.DoEvents();

                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                        bitmap.Save(System.IO.Path.Combine(args.RenderFolder,
                                    product.Id + "-" + page.ToString().ToLowerInvariant() + ".png"),
                                    System.Drawing.Imaging.ImageFormat.Png);
                    }
                }

                form.Close();
            }
            return SetupProgram.ExitOk;
        }
    }
}

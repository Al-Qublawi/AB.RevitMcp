// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ABAdvTools.UI
{
    /// <summary>
    /// About AB Adv Tools: which AB add-ins are loaded, their versions, and whether a newer
    /// release exists for each. Doubles as the "Check for Updates" window.
    /// </summary>
    internal sealed class AboutForm : Form
    {
        private readonly AdvToolsHost _host;
        private readonly List<AdvToolsProduct> _products;
        private readonly Dictionary<string, UpdateResult> _results = new Dictionary<string, UpdateResult>();
        private readonly bool _checkOnShow;

        private ListView _list;
        private Label _detail;
        private FlowLayoutPanel _toolActions;
        private string _actionsFor;
        private Button _checkButton;
        private Button _releaseButton;
        private CheckBox _notify;
        private Image _logo;
        private int _checksRunning;

        public AboutForm(AdvToolsHost host, List<AdvToolsProduct> products, bool checkOnShow)
        {
            _host = host;
            _products = products ?? new List<AdvToolsProduct>();
            _checkOnShow = checkOnShow;
            BuildUi();
            Populate();
        }

        private void BuildUi()
        {
            Text = "About " + AdvToolsBrand.SuiteName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(600, 540);
            BackColor = Color.White;

            // ---- banner ---------------------------------------------------
            var banner = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = AdvToolsUi.BannerBack };

            _logo = AdvToolsUi.LoadImage("abadv_64.png");
            int textLeft = 18;
            if (_logo != null)
            {
                banner.Controls.Add(new PictureBox
                {
                    Image = _logo,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Location = new Point(16, 14),
                    Size = new Size(68, 68),
                    BackColor = Color.Transparent
                });
                textLeft = 100;
            }

            banner.Controls.Add(new Label
            {
                Text = AdvToolsBrand.SuiteName,
                ForeColor = Color.White,
                Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(textLeft, 16),
                BackColor = Color.Transparent
            });
            banner.Controls.Add(new Label
            {
                Text = "Advanced BIM tools for Autodesk " + _host + "  -  by " + AdvToolsBrand.Author,
                ForeColor = AdvToolsUi.BannerSubtitle,
                AutoSize = true,
                Location = new Point(textLeft + 2, 48),
                BackColor = Color.Transparent
            });
            banner.Controls.Add(new Label
            {
                Text = "Everything AB lives on the " + AdvToolsBrand.RibbonTabName + " ribbon tab.",
                ForeColor = AdvToolsUi.BannerSubtitle,
                AutoSize = true,
                Location = new Point(textLeft + 2, 68),
                BackColor = Color.Transparent
            });

            // ---- body -----------------------------------------------------
            var heading = new Label
            {
                Text = "AB tools loaded in this " + _host + " session:",
                AutoSize = true,
                Location = new Point(16, 110)
            };

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Location = new Point(18, 132),
                Size = new Size(564, 120),
                BorderStyle = BorderStyle.FixedSingle
            };
            _list.Columns.Add("Tool", 220);
            _list.Columns.Add("Installed", 80);
            _list.Columns.Add("Latest", 80);
            _list.Columns.Add("Status", 160);
            _list.SelectedIndexChanged += delegate { UpdateSelection(); };
            _list.DoubleClick += delegate { OpenSelectedRelease(); };

            // What the selected tool says about itself: version, tagline, its own details (what its
            // About dialog used to show) and the latest release status.
            _detail = new Label
            {
                Location = new Point(16, 260),
                Size = new Size(568, 92),
                ForeColor = Color.FromArgb(70, 70, 70)
            };

            // The selected tool's own buttons, e.g. "Open the log folder".
            _toolActions = new FlowLayoutPanel
            {
                Location = new Point(14, 354),
                Size = new Size(572, 34),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            _checkButton = new Button { Text = "Check for updates", Location = new Point(18, 400), Size = new Size(150, 30) };
            _checkButton.Click += delegate { StartCheck(); };

            _releaseButton = new Button { Text = "Open release page", Location = new Point(176, 400), Size = new Size(150, 30), Enabled = false };
            _releaseButton.Click += delegate { OpenSelectedRelease(); };

            _notify = new CheckBox
            {
                Text = "Tell me when a new release of an AB tool is published",
                AutoSize = true,
                Location = new Point(20, 442),
                Checked = AdvToolsSettings.CheckForUpdates
            };
            _notify.CheckedChanged += delegate { AdvToolsSettings.CheckForUpdates = _notify.Checked; };

            var linkedIn = new LinkLabel
            {
                Text = AdvToolsBrand.LinkedInCaption,
                AutoSize = true,
                Location = new Point(18, 472),
                LinkColor = AdvToolsUi.LinkBlue,
                ActiveLinkColor = AdvToolsUi.LinkBlue,
                VisitedLinkColor = AdvToolsUi.LinkBlue,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            linkedIn.LinkClicked += delegate { AdvToolsUi.OpenLinkedIn(this); };

            var copyright = new Label
            {
                Text = AdvToolsBrand.Copyright + "   Kit " + AdvToolsBrand.KitVersion,
                AutoSize = true,
                ForeColor = Color.FromArgb(110, 110, 110),
                Location = new Point(18, 500)
            };

            var close = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Location = new Point(482, 494),
                Size = new Size(100, 30)
            };

            Controls.AddRange(new Control[]
            {
                banner, heading, _list, _detail, _toolActions, _checkButton, _releaseButton, _notify, linkedIn, copyright, close
            });
            AcceptButton = close;
            CancelButton = close;
        }

        private void Populate()
        {
            _list.Items.Clear();

            if (_products.Count == 0)
            {
                _detail.Text = "No AB tools have registered in this session.";
                _checkButton.Enabled = false;
                return;
            }

            foreach (AdvToolsProduct product in _products)
            {
                var item = new ListViewItem(product.Name) { Tag = product };
                item.SubItems.Add(product.Version);
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(product.HasRepository ? "Not checked" : "No release channel");
                _list.Items.Add(item);
            }

            _list.Items[0].Selected = true;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_checkOnShow) StartCheck();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_logo != null) { _logo.Dispose(); _logo = null; }
                if (_bold != null) { _bold.Dispose(); _bold = null; }
            }
            base.Dispose(disposing);
        }

        private Font _bold;

        private Font BoldFont()
        {
            return _bold ?? (_bold = new Font(_list.Font, FontStyle.Bold));
        }

        private void StartCheck()
        {
            if (_checksRunning > 0) return;

            _checkButton.Enabled = false;
            _checkButton.Text = "Checking...";

            foreach (ListViewItem item in _list.Items)
            {
                var product = (AdvToolsProduct)item.Tag;
                if (!product.HasRepository) continue;

                Interlocked.Increment(ref _checksRunning);
                item.SubItems[3].Text = "Checking...";

                AdvToolsProduct captured = product;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    UpdateResult result = UpdateChecker.CheckNow(captured);
                    PostToUi(delegate { ShowResult(captured, result); });
                });
            }

            if (_checksRunning == 0) FinishCheck();
        }

        private void ShowResult(AdvToolsProduct product, UpdateResult result)
        {
            if (IsDisposed) return;

            _results[product.Id] = result;

            foreach (ListViewItem item in _list.Items)
            {
                var rowProduct = item.Tag as AdvToolsProduct;
                if (rowProduct == null || rowProduct.Id != product.Id) continue;

                item.SubItems[2].Text = result != null && result.LatestVersion != null ? result.LatestVersion : "-";
                item.SubItems[3].Text = result != null ? result.StatusText : "No release information";

                bool update = result != null && result.Status == UpdateStatus.UpdateAvailable;
                item.ForeColor = update ? AdvToolsUi.UpdateGreen : SystemColors.WindowText;
                item.Font = update ? BoldFont() : _list.Font;
                if (update) item.Selected = true;
            }

            if (Interlocked.Decrement(ref _checksRunning) == 0) FinishCheck();
            UpdateSelection();
        }

        private void FinishCheck()
        {
            _checkButton.Text = "Check again";
            _checkButton.Enabled = true;
        }

        private void UpdateSelection()
        {
            AdvToolsProduct product = SelectedProduct();
            if (product == null)
            {
                _releaseButton.Enabled = false;
                _toolActions.Controls.Clear();
                _actionsFor = null;
                return;
            }

            _releaseButton.Enabled = product.HasRepository;

            UpdateResult result;
            _results.TryGetValue(product.Id, out result);

            string text = product.Name + " " + product.Version;
            if (!string.IsNullOrEmpty(product.Tagline)) text += "  -  " + product.Tagline;

            string details = SafeDetails(product);
            if (!string.IsNullOrEmpty(details)) text += Environment.NewLine + details;

            ShowToolActions(product);

            if (result != null)
            {
                if (result.Status == UpdateStatus.UpdateAvailable)
                {
                    text += Environment.NewLine + "Version " + result.LatestVersion +
                            " is available. Open the release page to download the installer.";
                    _releaseButton.Text = "Download update";
                }
                else
                {
                    if (!string.IsNullOrEmpty(result.Detail)) text += Environment.NewLine + result.Detail;
                    _releaseButton.Text = "Open release page";
                }
            }

            _detail.Text = text;
        }

        /// <summary>The tool's own lines. A throwing Details delegate must not break the dialog.</summary>
        private static string SafeDetails(AdvToolsProduct product)
        {
            if (product.Details == null) return null;
            try
            {
                string details = product.Details();
                return details == null ? null : details.Trim();
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn(product.Id + " details failed: " + ex.Message);
                return null;
            }
        }

        private void ShowToolActions(AdvToolsProduct product)
        {
            if (_actionsFor == product.Id) return;
            _actionsFor = product.Id;

            _toolActions.Controls.Clear();
            foreach (AdvToolsAction action in product.Actions)
            {
                AdvToolsAction captured = action;
                var button = new Button { Text = action.Text, AutoSize = true, Height = 28, Margin = new Padding(3, 2, 6, 2) };
                button.Click += delegate
                {
                    try { captured.Run(); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, product.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                _toolActions.Controls.Add(button);
            }
        }

        private AdvToolsProduct SelectedProduct()
        {
            if (_list.SelectedItems.Count == 0) return null;
            return _list.SelectedItems[0].Tag as AdvToolsProduct;
        }

        private void OpenSelectedRelease()
        {
            AdvToolsProduct product = SelectedProduct();
            if (product == null || !product.HasRepository) return;

            UpdateResult result;
            string url = _results.TryGetValue(product.Id, out result) && result != null && !string.IsNullOrEmpty(result.ReleaseUrl)
                ? result.ReleaseUrl
                : product.LatestReleasePageUrl;

            AdvToolsBrand.OpenUrl(url);
        }

        private void PostToUi(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The dialog closed while a check was still running. Nothing to update.
            }
        }
    }
}

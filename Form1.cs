using System.ComponentModel;
using System.Drawing;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using eZBERP_AI_IDE.Services;

namespace eZBERP_AI_IDE
{
    public partial class Form1 : Form
    {
        private const string PlaceholderText = "Type your command here... (e.g., 'Deploy to sandbox')";
        private const int BalanceCheckIntervalMs = 300000;

        private readonly string _apiKey = AiProviderSettings.ApiKey;
        private readonly DeepSeekClient _deepSeekClient;
        private readonly SalesforceCliService _salesforceCliService;
        private readonly SalesforceValidationService _salesforceValidationService;
        private readonly RepoContextService _repoContextService;
        private readonly StoryAnalyzerService _storyAnalyzerService;
        private readonly ConfigMetadataOrchestrator _configMetadataOrchestrator;
        private readonly JiraService _jiraService;

        private string? _selectedRepoPath;
        private string? _selectedOrgAlias;
        private TextBox txtCommandInput = null!;
        private System.Windows.Forms.Timer _balanceCheckTimer = null!;
        private DataGridView dgvJiraStories = null!;
        private TextBox txtJiraSearch = null!;
        private TextBox txtJiraSpace = null!;
        private TextBox txtJiraSprint = null!;
        private ComboBox cmbJiraType = null!;
        private ComboBox cmbJiraStatus = null!;
        private ComboBox cmbJiraLeadConsultant = null!;
        private Button btnLoadJiraStories = null!;
        private Button btnProcessJiraStory = null!;
        private Button btnReviewGitChanges = null!;
        private Label lblJiraStatus = null!;
        private Panel pnlJiraStoryPreview = null!;
        private WebBrowser wbJiraStoryPreview = null!;
        private Label lblJiraStoryPreviewTitle = null!;
        private Button btnCloseJiraPreview = null!;
        private ContextMenuStrip jiraStoryContextMenu = null!;
        private JiraWorkItem? _contextJiraStory;
        private readonly BindingList<JiraWorkItem> _jiraStories = new();
        private readonly Dictionary<string, JiraCoverageAnalysisCacheEntry> _jiraCoverageAnalysisCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _includeCoverageAlternatives;

        private sealed class JiraCoverageAnalysisCacheEntry
        {
            public string StoryText { get; init; } = string.Empty;
            public SalesforceConfigPlan NormalizedPlan { get; init; } = new();
            public SalesforceConfigCoverage Coverage { get; init; } = new();
            public string CoverageHtml { get; init; } = string.Empty;
        }

        public Form1()
        {
            _deepSeekClient = new DeepSeekClient(_apiKey);
            _salesforceCliService = new SalesforceCliService();
            _salesforceValidationService = new SalesforceValidationService(_salesforceCliService);
            _repoContextService = new RepoContextService();
            _storyAnalyzerService = new StoryAnalyzerService(_deepSeekClient);
            _jiraService = new JiraService();
            _configMetadataOrchestrator = new ConfigMetadataOrchestrator(new IConfigWorkItemHandler[]
            {
                new PermissionManagementService()
            }, _deepSeekClient, ReportProcessingStep);
            InitializeComponent();
            InitializeCustomComponents();
            InitializeEngine();
        }

        private void InitializeCustomComponents()
        {
            ConfigureJiraStoryPicker();
            ConfigureGitReviewButton();
            ConfigureCommandInput();
            ConfigureSendButton();
            ConfigureChatArea();
            ConfigureLabels();
            ConfigureButtons();
            ConfigureForm();
            WireEvents();
        }

        private void ConfigureJiraStoryPicker()
        {
            var labelForeColor = Color.FromArgb(220, 220, 220);
            var inputBackColor = Color.FromArgb(35, 35, 35);

            txtJiraSearch = CreateFilterTextBox("Search work", new Point(24, 68), new Size(170, 28));
            txtJiraSpace = CreateFilterTextBox(GetEnvironmentSetting("JIRA_PROJECT_KEY", "PNX"), new Point(204, 68), new Size(110, 28));
            cmbJiraType = CreateFilterComboBox(new Point(324, 68), new Size(90, 28), "Story", "Task", "Bug");
            cmbJiraStatus = CreateFilterComboBox(new Point(424, 68), new Size(120, 28), string.Empty, "To Do", "In Development", "Done");
            cmbJiraLeadConsultant = CreateFilterComboBox(new Point(554, 68), new Size(145, 28), "Current User");
            txtJiraSprint = CreateFilterTextBox("Phoenix_94.0_Sprint 1", new Point(710, 68), new Size(190, 28));
            ForceJiraFilterColors();

            btnLoadJiraStories = new Button
            {
                Text = "Load Jira Stories",
                Location = new Point(910, 68),
                Size = new Size(120, 28)
            };

            btnProcessJiraStory = new Button
            {
                Text = "Process Selected",
                Location = new Point(1040, 68),
                Size = new Size(120, 28),
                Enabled = false
            };

            lblJiraStatus = new Label
            {
                Location = new Point(24, 100),
                Size = new Size(1136, 18),
                ForeColor = labelForeColor,
                Text = "Jira stories: not loaded"
            };

            dgvJiraStories = new DataGridView
            {
                Location = new Point(24, 122),
                Size = new Size(1136, 220),
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                ReadOnly = true,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                ForeColor = Color.Black,
                GridColor = Color.FromArgb(190, 190, 190),
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 26,
                DataSource = _jiraStories
            };
            dgvJiraStories.DefaultCellStyle.BackColor = Color.White;
            dgvJiraStories.DefaultCellStyle.ForeColor = Color.Black;
            dgvJiraStories.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
            dgvJiraStories.DefaultCellStyle.SelectionForeColor = Color.White;
            dgvJiraStories.RowsDefaultCellStyle.BackColor = Color.White;
            dgvJiraStories.RowsDefaultCellStyle.ForeColor = Color.Black;
            dgvJiraStories.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            dgvJiraStories.AlternatingRowsDefaultCellStyle.ForeColor = Color.Black;
            dgvJiraStories.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
            dgvJiraStories.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.White;
            dgvJiraStories.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            dgvJiraStories.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
            dgvJiraStories.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(245, 245, 245);
            dgvJiraStories.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.Black;
            dgvJiraStories.RowTemplate.Height = 24;
            dgvJiraStories.RowTemplate.Resizable = DataGridViewTriState.False;
            dgvJiraStories.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            dgvJiraStories.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            dgvJiraStories.EnableHeadersVisualStyles = false;
            dgvJiraStories.SelectionChanged += (_, _) => btnProcessJiraStory.Enabled = dgvJiraStories.SelectedRows.Count > 0;
            dgvJiraStories.CellMouseDown += DgvJiraStories_CellMouseDown!;
            dgvJiraStories.RowsAdded += (_, args) => FixJiraGridRows(args.RowIndex, args.RowCount);
            ConfigureJiraStoryContextMenu();

            AddJiraColumn("Key", "Key", 95);
            AddJiraColumn("Summary", "Summary", 420);
            AddJiraColumn("Sprint", "Sprint", 160);
            AddJiraColumn("Status", "Status", 130);
            AddJiraColumn("Fix Versions", "FixVersions", 120);
            AddJiraColumn("Story Points", "StoryPoints", 90);
            AddJiraColumn("Assignee", "Assignee", 120);

            Controls.Add(txtJiraSearch);
            Controls.Add(txtJiraSpace);
            Controls.Add(cmbJiraType);
            Controls.Add(cmbJiraStatus);
            Controls.Add(cmbJiraLeadConsultant);
            Controls.Add(txtJiraSprint);
            Controls.Add(btnLoadJiraStories);
            Controls.Add(btnProcessJiraStory);
            Controls.Add(lblJiraStatus);
            Controls.Add(dgvJiraStories);
            ConfigureJiraStoryPreview();
        }

        private void ConfigureGitReviewButton()
        {
            btnReviewGitChanges = new Button
            {
                Text = "Review Git",
                Location = new Point(288, 20),
                Size = new Size(120, 32),
                Enabled = false
            };

            Controls.Add(btnReviewGitChanges);
        }

        private void ForceJiraFilterColors()
        {
            foreach (var control in new Control[] { txtJiraSearch, txtJiraSpace, cmbJiraType, cmbJiraStatus, cmbJiraLeadConsultant, txtJiraSprint })
            {
                control.BackColor = Color.FromArgb(35, 35, 35);
                control.ForeColor = Color.White;
            }
        }
        private TextBox CreateFilterTextBox(string text, Point location, Size size)
        {
            return new TextBox
            {
                Location = location,
                Size = size,
                Text = text,
                BackColor = Color.FromArgb(35, 35, 35),
                ForeColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private ComboBox CreateFilterComboBox(Point location, Size size, params string[] values)
        {
            var comboBox = new ComboBox
            {
                Location = location,
                Size = size,
                DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = Color.FromArgb(35, 35, 35),
                ForeColor = Color.White
            };
            comboBox.Items.AddRange(values.Cast<object>().ToArray());
            comboBox.Text = values.FirstOrDefault() ?? string.Empty;
            return comboBox;
        }

        private void ConfigureJiraStoryContextMenu()
        {
            jiraStoryContextMenu = new ContextMenuStrip();
            jiraStoryContextMenu.Items.Add("Show story details", null, async (_, _) =>
            {
                if (_contextJiraStory is not null)
                {
                    await ShowJiraStoryHtmlPreviewAsync(_contextJiraStory);
                }
            });
            jiraStoryContextMenu.Items.Add("Analyze AI Coverage for solution", null, async (_, _) =>
            {
                if (_contextJiraStory is not null)
                {
                    await ShowJiraCoveragePreviewAsync(_contextJiraStory);
                }
            });
            jiraStoryContextMenu.Items.Add("Test image reading", null, async (_, _) =>
            {
                if (_contextJiraStory is not null)
                {
                    await ShowJiraImageReadingDiagnosticAsync(_contextJiraStory);
                }
            });
        }

        private void ConfigureJiraStoryPreview()
        {
            pnlJiraStoryPreview = new Panel
            {
                Location = new Point(120, 82),
                Size = new Size(930, 520),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };

            lblJiraStoryPreviewTitle = new Label
            {
                Location = new Point(14, 10),
                Size = new Size(800, 28),
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Text = "Jira Story Preview"
            };

            btnCloseJiraPreview = new Button
            {
                Text = "Close",
                Location = new Point(840, 8),
                Size = new Size(75, 28),
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnCloseJiraPreview.Click += (_, _) => pnlJiraStoryPreview.Visible = false;

            wbJiraStoryPreview = new WebBrowser
            {
                Location = new Point(14, 44),
                Size = new Size(900, 460),
                ScriptErrorsSuppressed = true,
                AllowWebBrowserDrop = false,
                WebBrowserShortcutsEnabled = false
            };

            pnlJiraStoryPreview.Controls.Add(lblJiraStoryPreviewTitle);
            pnlJiraStoryPreview.Controls.Add(btnCloseJiraPreview);
            pnlJiraStoryPreview.Controls.Add(wbJiraStoryPreview);
            Controls.Add(pnlJiraStoryPreview);
            pnlJiraStoryPreview.BringToFront();
        }

        private void DgvJiraStories_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.Button != MouseButtons.Right)
            {
                return;
            }

            dgvJiraStories.ClearSelection();
            dgvJiraStories.Rows[e.RowIndex].Selected = true;
            _contextJiraStory = dgvJiraStories.Rows[e.RowIndex].DataBoundItem as JiraWorkItem;
            if (_contextJiraStory is not null)
            {
                jiraStoryContextMenu.Show(dgvJiraStories, e.Location);
            }
        }

        private async Task ShowJiraStoryHtmlPreviewAsync(JiraWorkItem story)
        {
            pnlJiraStoryPreview.Visible = true;
            pnlJiraStoryPreview.BringToFront();
            lblJiraStoryPreviewTitle.Text = $"{story.Key} - {story.Summary}";
            SetJiraPreviewHtml(BuildSimpleHtml("Loading story content...", "Please wait while Jira content is loaded."));

            try
            {
                SetJiraPreviewHtml(await _jiraService.GetStoryHtmlAsync(story.Key));
            }
            catch (Exception ex)
            {
                SetJiraPreviewHtml(BuildSimpleHtml("Could not load Jira story content", WebUtility.HtmlEncode(ex.Message)));
            }
        }

        private async Task ShowJiraCoveragePreviewAsync(JiraWorkItem story)
        {
            if (string.IsNullOrWhiteSpace(_selectedRepoPath))
            {
                AppendToChat("No repository selected. Please select your Salesforce repo first.", Color.Red);
                return;
            }

            pnlJiraStoryPreview.Visible = true;
            pnlJiraStoryPreview.BringToFront();
            lblJiraStoryPreviewTitle.Text = $"Coverage - {story.Key}";

            if (TryGetCachedJiraCoverageAnalysis(story, out var cachedAnalysis))
            {
                SetJiraPreviewHtml(cachedAnalysis.CoverageHtml);
                AppendToChat($"Using cached coverage analysis for {story.Key}.", Color.Gray);
                return;
            }

            try
            {
                var analysis = await BuildJiraCoverageAnalysisAsync(story, showProgress: true);
                SetJiraPreviewHtml(analysis.CoverageHtml);
            }
            catch (Exception ex)
            {
                SetJiraPreviewHtml(BuildSimpleHtml("Could not analyze coverage", WebUtility.HtmlEncode(ex.Message)));
            }
        }

        private async Task ShowJiraImageReadingDiagnosticAsync(JiraWorkItem story)
        {
            pnlJiraStoryPreview.Visible = true;
            pnlJiraStoryPreview.BringToFront();
            lblJiraStoryPreviewTitle.Text = $"Image reading test - {story.Key}";
            SetJiraPreviewHtml(BuildSimpleHtml("Testing image reading", "Loading Jira story images and asking the vision model what it can read..."));

            try
            {
                var storyContent = await _jiraService.GetStoryAnalysisContentAsync(story.Key);
                var imageBlocks = storyContent.Blocks
                    .Where(block => block.Kind.Equals("image", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var diagnostic = await _storyAnalyzerService.DescribeInlineImagesAsync(storyContent);
                SetJiraPreviewHtml(BuildImageReadingDiagnosticHtml(story, imageBlocks, diagnostic));
            }
            catch (Exception ex)
            {
                SetJiraPreviewHtml(BuildSimpleHtml("Could not test image reading", WebUtility.HtmlEncode(ex.Message)));
            }
        }

        private bool TryGetCachedJiraCoverageAnalysis(JiraWorkItem story, out JiraCoverageAnalysisCacheEntry analysis)
        {
            analysis = null!;

            if (string.IsNullOrWhiteSpace(_selectedRepoPath))
            {
                return false;
            }

            var cacheKey = BuildJiraCoverageCacheKey(story.Key, _selectedRepoPath!);
            return _jiraCoverageAnalysisCache.TryGetValue(cacheKey, out analysis!);
        }

        private async Task<JiraCoverageAnalysisCacheEntry> BuildJiraCoverageAnalysisAsync(JiraWorkItem story, bool showProgress)
        {
            if (TryGetCachedJiraCoverageAnalysis(story, out var cachedAnalysis))
            {
                return cachedAnalysis;
            }

            if (showProgress)
            {
                ShowCoverageProgress(story, 0, "Starting analysis workspace...");
                await Task.Delay(80);
                ShowCoverageProgress(story, 1, "Loading Jira story content, including description and acceptance criteria...");
            }

            var storyContent = await _jiraService.GetStoryAnalysisContentAsync(story.Key);
            var storyText = storyContent.PlainText;

            if (showProgress)
            {
                var analysisProvider = storyContent.HasInlineImages && AiProviderSettings.UseOpenAiForInlineImages
                    ? $"Asking OpenAI vision to extract Salesforce requirements from {storyContent.Blocks.Count(block => block.Kind.Equals("image", StringComparison.OrdinalIgnoreCase))} inline image(s)..."
                    : "Asking AI to extract Salesforce config requirements from the story...";
                ShowCoverageProgress(story, 2, analysisProvider);
            }

            var configPlan = await _storyAnalyzerService.AnalyzeAsync(_selectedRepoPath!, storyContent);

            if (showProgress)
            {
                ShowCoverageProgress(story, 3, "Normalizing extracted requirements against the Salesforce config engine...");
            }

            var normalizedPlan = _configMetadataOrchestrator.NormalizePlan(configPlan);

            if (showProgress)
            {
                ShowCoverageProgress(story, 4, "Assessing what can be handled automatically and what still needs manual work...");
            }

            var coverage = await _configMetadataOrchestrator.AssessCoverageAsync(_selectedRepoPath!, normalizedPlan);

            if (showProgress)
            {
                ShowCoverageProgress(story, 6, "Building the coverage report...");
            }

            var analysis = new JiraCoverageAnalysisCacheEntry
            {
                StoryText = storyText,
                NormalizedPlan = normalizedPlan,
                Coverage = coverage,
                CoverageHtml = BuildCoverageHtml(story, coverage)
            };

            var cacheKey = BuildJiraCoverageCacheKey(story.Key, _selectedRepoPath!);
            _jiraCoverageAnalysisCache[cacheKey] = analysis;
            return analysis;
        }

        private void ShowCoverageProgress(JiraWorkItem story, int activeStep, string message)
        {
            SetJiraPreviewHtml(BuildCoverageProgressHtml(story, activeStep, message));
            Application.DoEvents();
        }

        private void SetJiraPreviewHtml(string html)
        {
            var safeHtml = string.IsNullOrWhiteSpace(html)
                ? BuildSimpleHtml("No preview content", "The preview renderer received an empty HTML document.")
                : html;

            try
            {
                if (wbJiraStoryPreview.Document is null)
                {
                    wbJiraStoryPreview.DocumentText = safeHtml;
                    return;
                }

                wbJiraStoryPreview.Document.OpenNew(true);
                wbJiraStoryPreview.Document.Write(safeHtml);
                wbJiraStoryPreview.Refresh(WebBrowserRefreshOption.Completely);
            }
            catch
            {
                wbJiraStoryPreview.DocumentText = safeHtml;
            }
        }

        private static string BuildCoverageProgressHtml(JiraWorkItem story, int activeStep, string message)
        {
            string[] steps =
            {
                "Prepare analysis",
                "Load Jira story",
                "Extract requirements with AI",
                "Normalize requirements",
                "Detect unsupported work",
                "Assess coverage",
                "Render report"
            };

            var builder = new StringBuilder();
            var thinkingSpinnerSizePx = GetThinkingSpinnerSizePx();
            builder.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><style>");
            builder.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;background:#f5f7fb;color:#1f2933;margin:0;padding:24px}.card{background:white;border:1px solid #d9e2ec;border-radius:14px;padding:24px;box-shadow:0 10px 26px rgba(15,23,42,.10)}h1{font-size:20px;margin:0;color:#102a43}.sub{color:#52606d;margin:8px 0 20px}.message{background:#e6f0ff;border-left:4px solid #0967d2;border-radius:8px;padding:12px 14px;margin:18px 0;color:#102a43;font-weight:600}.steps{margin-top:14px}.step{display:flex;align-items:center;gap:10px;padding:10px 0;border-top:1px solid #edf2f7}.dot{width:14px;height:14px;border-radius:999px;background:#cbd5e1;display:inline-flex;align-items:center;justify-content:center;flex:0 0 14px}.done .dot{background:#138a36}.done .dot:after{content:\"\";width:7px;height:4px;border-left:2px solid white;border-bottom:2px solid white;transform:rotate(-45deg);margin-top:-1px}.active .dot{background:transparent;border:2px solid #bfdbfe;border-top-color:#0967d2;animation:spin .8s linear infinite;box-sizing:border-box}.done .label{color:#138a36}.active .label{font-weight:700;color:#0967d2}.thinkingSpinner{display:none;width:" + thinkingSpinnerSizePx + "px;height:" + thinkingSpinnerSizePx + "px;object-fit:contain;margin-left:8px;vertical-align:middle}.active .thinkingSpinner{display:inline-block}.hint{margin-top:20px;color:#64748b;font-size:12px}");
            builder.AppendLine("</style></head><body><div class=\"card\">");
            builder.AppendLine($"<h1>Analyzing coverage for {WebUtility.HtmlEncode(story.Key)}</h1>");
            builder.AppendLine($"<div class=\"sub\">{WebUtility.HtmlEncode(story.Summary)}</div>");
            builder.AppendLine($"<div class=\"message\">{WebUtility.HtmlEncode(message)}</div>");
            builder.AppendLine("<div class=\"steps\">");
            var thinkingSpinnerDataUri = BuildThinkingSpinnerDataUri();

            for (var i = 0; i < steps.Length; i++)
            {
                var cssClass = i < activeStep ? "step done" : i == activeStep ? "step active" : "step";
                var spinnerImage = i == activeStep && !string.IsNullOrWhiteSpace(thinkingSpinnerDataUri)
                    ? $"<img class=\"thinkingSpinner\" src=\"{thinkingSpinnerDataUri}\" alt=\"Thinking\" />"
                    : string.Empty;
                builder.AppendLine($"<div class=\"{cssClass}\"><span class=\"dot\"></span><span class=\"label\">{WebUtility.HtmlEncode(steps[i])}</span>{spinnerImage}</div>");
            }

            builder.AppendLine("</div><div class=\"hint\">This can take a moment because the story is being interpreted and mapped to supported Salesforce config services.</div>");
            builder.AppendLine("</div></body></html>");
            return builder.ToString();
        }

        private static string BuildThinkingSpinnerDataUri()
        {
            var spinnerPath = GetEnvironmentSetting("EZBERP_LOADING_GIF_PATH", "");
            if (!System.IO.File.Exists(spinnerPath)) return string.Empty;
            var bytes = System.IO.File.ReadAllBytes(spinnerPath);
            return $"data:image/gif;base64,{Convert.ToBase64String(bytes)}";
        }

        private static int GetThinkingSpinnerSizePx()
        {
            var rawSize = GetEnvironmentSetting("EZBERP_THINKING_GIF_SIZE", "44").Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            return int.TryParse(rawSize, out var size) && size is >= 12 and <= 160 ? size : 44;
        }

        private static string GetEnvironmentSetting(string name, string fallback)
        {
            return FirstNonBlankSetting(
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process),
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine),
                fallback);
        }

        private static string FirstNonBlankSetting(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

        private static string BuildJiraCoverageCacheKey(string storyKey, string repoPath)
        {
            var normalizedRepoPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var visionMode = AiProviderSettings.UseOpenAiForInlineImages ? "vision-auto" : "text-only";
            const string coverageAnalyzerCacheVersion = "coverage-v16-permissions-only";
            return $"{coverageAnalyzerCacheVersion}|{visionMode}|{normalizedRepoPath}|{storyKey}";
        }

        private static string BuildSimpleHtml(string title, string body)
        {
            return $@"<!doctype html><html><head><meta charset=""utf-8""><style>
body {{ font-family: Segoe UI, Arial, sans-serif; background:#f5f7fb; color:#1f2933; margin:0; padding:24px; }}
.card {{ background:white; border:1px solid #d9e2ec; border-radius:10px; padding:20px; box-shadow:0 8px 20px rgba(15,23,42,.08); }}
h1 {{ margin-top:0; font-size:20px; color:#102a43; }}
</style></head><body><div class=""card""><h1>{WebUtility.HtmlEncode(title)}</h1><p>{body}</p></div></body></html>";
        }

        private static string BuildImageReadingDiagnosticHtml(JiraWorkItem story, IReadOnlyList<JiraStoryAnalysisBlock> imageBlocks, string diagnostic)
        {
            var imageRows = imageBlocks.Count == 0
                ? "<li>No image blocks were found.</li>"
                : string.Join(Environment.NewLine, imageBlocks.Select(block => $"<li><strong>{WebUtility.HtmlEncode(block.FileName)}</strong><br><code>{WebUtility.HtmlEncode(block.LocalPath)}</code></li>"));

            return $@"<!doctype html><html><head><meta charset=""utf-8""><style>
body {{ font-family: Segoe UI, Arial, sans-serif; background:#f5f7fb; color:#1f2933; margin:0; padding:24px; }}
.card {{ background:white; border:1px solid #d9e2ec; border-radius:10px; padding:20px; box-shadow:0 8px 20px rgba(15,23,42,.08); }}
h1 {{ margin-top:0; font-size:20px; color:#102a43; }}
pre {{ white-space:pre-wrap; background:#0f172a; color:#e2e8f0; border-radius:8px; padding:14px; font-family:Consolas, monospace; }}
</style></head><body><div class=""card""><h1>{WebUtility.HtmlEncode(story.Key)} diagnostic</h1><ul>{imageRows}</ul><h2>Vision response</h2><pre>{WebUtility.HtmlEncode(diagnostic)}</pre></div></body></html>";
        }

        private static string BuildCoverageHtml(JiraWorkItem story, SalesforceConfigCoverage coverage)
        {
            var supported = coverage.Results.Where(result => result.IsSupported).ToList();
            var unsupported = coverage.Results.Where(result => !result.IsSupported).ToList();
            var accent = coverage.UnsupportedRequirements == 0 ? "#138a36" : "#b7791f";

            var builder = new StringBuilder();
            builder.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><style>");
            builder.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;background:#f5f7fb;color:#1f2933;margin:0;padding:22px}.card{background:white;border:1px solid #d9e2ec;border-radius:12px;padding:20px;box-shadow:0 8px 20px rgba(15,23,42,.08)}h1{font-size:20px;margin:0 0 6px;color:#102a43}.sub{color:#52606d;margin-bottom:18px}.score{display:inline-block;background:" + accent + ";color:white;border-radius:999px;padding:8px 14px;font-weight:700}.grid{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin-top:18px}.panel{border:1px solid #d9e2ec;border-radius:10px;padding:14px;background:#fbfdff}.panel h2{font-size:15px;margin:0 0 10px}.item{padding:10px 0;border-top:1px solid #edf2f7}.item:first-of-type{border-top:0}.reason{color:#52606d;font-size:12px;margin-top:4px}.ok{color:#138a36}.no{color:#b42318}");
            builder.AppendLine("</style></head><body><div class=\"card\">");
            builder.AppendLine($"<h1>{WebUtility.HtmlEncode(story.Key)} coverage</h1>");
            builder.AppendLine($"<span class=\"score\">{coverage.SupportedRequirements} of {coverage.TotalRequirements} supported</span>");
            builder.AppendLine("<div class=\"grid\"><div class=\"panel\"><h2 class=\"ok\">Supported</h2>");
            AppendCoverageItems(builder, supported);
            builder.AppendLine("</div><div class=\"panel\"><h2 class=\"no\">Unsupported</h2>");
            AppendCoverageItems(builder, unsupported);
            builder.AppendLine("</div></div></div></body></html>");
            return builder.ToString();
        }

        private static void AppendCoverageItems(StringBuilder builder, IReadOnlyList<RequirementCoverageResult> results)
        {
            if (results.Count == 0) { builder.AppendLine("<div class=\"item\">None</div>"); return; }
            foreach (var result in results)
            {
                builder.AppendLine("<div class=\"item\">");
                builder.AppendLine($"<strong>{WebUtility.HtmlEncode(SalesforceConfigPlanFormatter.BuildRequirementHeadline(result.Requirement))}</strong>");
                builder.AppendLine($"<div class=\"reason\">{WebUtility.HtmlEncode(result.Reason)}</div>");
                builder.AppendLine("</div>");
            }
        }

        private void FixJiraGridRows(int rowIndex, int rowCount)
        {
            for (var i = rowIndex; i < rowIndex + rowCount && i < dgvJiraStories.Rows.Count; i++)
            {
                dgvJiraStories.Rows[i].Height = 24;
                dgvJiraStories.Rows[i].Resizable = DataGridViewTriState.False;
            }
        }

        private void AddJiraColumn(string headerText, string dataPropertyName, int width) => dgvJiraStories.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = headerText, DataPropertyName = dataPropertyName, Width = width });

        private void ConfigureCommandInput() => txtCommandInput = new TextBox { Multiline = true, Visible = false, Enabled = false };
        private void ConfigureSendButton() { btnSend.Visible = false; btnSend.Enabled = false; }
        private void ConfigureChatArea() { rtbChat.Location = new Point(24, 360); rtbChat.Size = new Size(1136, 300); rtbChat.ReadOnly = true; rtbChat.BackColor = Color.FromArgb(30, 30, 30); rtbChat.ForeColor = Color.White; }
        private void ConfigureLabels() { lblRemainingBalance.Location = new Point(920, 8); lblSelectedOrg.Location = new Point(920, 30); lblSelectedRepo.Location = new Point(24, 684); lblProcessing.Location = new Point(770, 684); }
        private void ConfigureButtons() { StyleButton(btnSelectOrg); StyleButton(btnSelectRepo); StyleButton(btnReviewGitChanges); StyleButton(btnLoadJiraStories); StyleButton(btnProcessJiraStory); }
        private void ConfigureForm() { Text = "Salesforce AI IDE - Permissions Tooling Only"; Size = new Size(1210, 760); BackColor = Color.FromArgb(45, 45, 48); }
        private void WireEvents() { btnSelectOrg.Click += BtnSelectOrg_Click!; btnSelectRepo.Click += BtnSelectRepo_Click!; btnReviewGitChanges.Click += BtnReviewGitChanges_Click!; btnLoadJiraStories.Click += BtnLoadJiraStories_Click!; btnProcessJiraStory.Click += BtnProcessJiraStory_Click!; }
        private void StyleButton(Button b) { b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.FromArgb(60, 60, 65); b.ForeColor = Color.White; }

        private void SetProcessingState(bool p, string m = "Processing...")
        {
            picLoading.Visible = p; lblProcessing.Visible = p; lblProcessing.Text = m;
            btnSelectOrg.Enabled = !p; btnSelectRepo.Enabled = !p; btnLoadJiraStories.Enabled = !p;
            btnReviewGitChanges.Enabled = !p && !string.IsNullOrWhiteSpace(_selectedRepoPath);
            btnProcessJiraStory.Enabled = !p && dgvJiraStories.SelectedRows.Count > 0;
            dgvJiraStories.Enabled = !p;
        }

        private void ReportProcessingStep(string m) { if (InvokeRequired) { BeginInvoke(new Action(() => ReportProcessingStep(m))); return; } lblProcessing.Text = m; AppendToChat(m, Color.LightBlue); }
        private void AlignProcessingIndicator() { }
        private void InitializeAiLogo() { }
        private void InitializeLoadingImage() { }
        private void InitializeEngine() { AppendToChat("Permissions Engine Initialized.", Color.Gray); _balanceCheckTimer = new System.Windows.Forms.Timer { Interval = BalanceCheckIntervalMs }; _balanceCheckTimer.Tick += async (_, _) => await CheckRemainingBalance(); _balanceCheckTimer.Start(); _ = CheckRemainingBalance(); }

        private async void BtnSelectOrg_Click(object sender, EventArgs e)
        {
            try { var orgs = await _salesforceCliService.GetOrgListAsync(); using var d = CreateOrgSelectionDialog(orgs); d.ShowDialog(); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private Form CreateOrgSelectionDialog(IReadOnlyList<OrgInfo> orgs)
        {
            var d = new Form { Text = "Select Org", Size = new Size(400, 300) };
            var lb = new ListBox { Dock = DockStyle.Fill };
            foreach (var o in orgs) lb.Items.Add(o.Alias);
            var b = new Button { Text = "Select", Dock = DockStyle.Bottom };
            b.Click += (_, _) => { if (lb.SelectedIndex >= 0) { _selectedOrgAlias = orgs[lb.SelectedIndex].Alias; lblSelectedOrg.Text = $"Org: {_selectedOrgAlias}"; d.DialogResult = DialogResult.OK; d.Close(); } };
            d.Controls.Add(lb); d.Controls.Add(b); return d;
        }

        private async void BtnLoadJiraStories_Click(object sender, EventArgs e)
        {
            SetProcessingState(true, "Loading Jira...");
            try { var stories = await _jiraService.SearchStoriesAsync(BuildJiraStoryFilter()); _jiraStories.Clear(); foreach (var s in stories) _jiraStories.Add(s); lblJiraStatus.Text = $"{stories.Count} stories loaded"; }
            catch (Exception ex) { AppendToChat(ex.Message, Color.Red); }
            finally { SetProcessingState(false); }
        }

        private async void BtnProcessJiraStory_Click(object sender, EventArgs e)
        {
            var s = GetSelectedJiraStory(); if (s == null) return;
            SetProcessingState(true, $"Processing {s.Key}...");
            try { var response = await ProcessSelectedJiraStoryAsync(s); AppendToChat($"AI: {response}", Color.White); }
            catch (Exception ex) { AppendToChat(ex.Message, Color.Red); }
            finally { SetProcessingState(false); }
        }

        private async Task<string> ProcessSelectedJiraStoryAsync(JiraWorkItem story)
        {
            var analysis = await BuildJiraCoverageAnalysisAsync(story, true);
            return await ProcessSalesforceConfigCoverageAsync(analysis.Coverage, analysis.StoryText);
        }

        private void BtnReviewGitChanges_Click(object sender, EventArgs e) { if (!string.IsNullOrEmpty(_selectedRepoPath)) new GitReviewForm(_selectedRepoPath).ShowDialog(this); }

        private async void BtnSelectRepo_Click(object sender, EventArgs e)
        {
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog() == DialogResult.OK) { _selectedRepoPath = d.SelectedPath; lblSelectedRepo.Text = $"Repo: {_selectedRepoPath}"; btnReviewGitChanges.Enabled = true; }
        }

        private async Task CheckRemainingBalance() { var b = await _deepSeekClient.GetBalanceAsync(); lblRemainingBalance.Text = b.Text; }

        private async Task<string> ProcessWithDeepSeekAsync(string cmd)
        {
            if (cmd.Contains("deploy")) return await DeployToOrgAsync();
            if (_storyAnalyzerService.IsSalesforceConfigRequest(cmd))
            {
                var plan = await _storyAnalyzerService.AnalyzeAsync(_selectedRepoPath!, cmd);
                var normalized = _configMetadataOrchestrator.NormalizePlan(plan);
                var coverage = await _configMetadataOrchestrator.AssessCoverageAsync(_selectedRepoPath!, normalized);
                return await ProcessSalesforceConfigCoverageAsync(coverage);
            }
            return "Command not recognized or unsupported in this focused version.";
        }

        private async Task<string> ProcessSalesforceConfigCoverageAsync(SalesforceConfigCoverage coverage, string storyText = "")
        {
            if (coverage.SupportedRequirements == 0) return "No supported requirements found.";
            if (!ApproveConfigPlan(coverage.SupportedPlan)) return "Approval declined.";
            var changeSet = await _configMetadataOrchestrator.BuildChangeSetAsync(_selectedRepoPath!, coverage.SupportedPlan);
            if (changeSet.Files.Count == 0) return "No changes generated.";
            if (!ApproveChangeSet(changeSet)) return "Change approval declined.";
            return await ApplyAndDeployChangeSetAsync(changeSet);
        }

        private bool ApproveConfigPlan(SalesforceConfigPlan p) { using var d = CreateConfigPlanApprovalDialog(p); return d.ShowDialog() == DialogResult.OK; }
        private Form CreateConfigPlanApprovalDialog(SalesforceConfigPlan p) { var d = new Form { Text = "Approve Roadmap", Size = new Size(600, 400) }; var rtb = new RichTextBox { Dock = DockStyle.Fill, Text = SalesforceConfigPlanFormatter.BuildPreview(p) }; var btn = new Button { Text = "Approve", Dock = DockStyle.Bottom }; btn.Click += (_, _) => { d.DialogResult = DialogResult.OK; d.Close(); }; d.Controls.Add(rtb); d.Controls.Add(btn); return d; }

        private bool ApproveChangeSet(FileChangeSet cs) { using var d = CreateChangeApprovalDialog(cs); return d.ShowDialog() == DialogResult.OK; }
        private Form CreateChangeApprovalDialog(FileChangeSet cs) { var d = new Form { Text = "Approve Changes", Size = new Size(800, 600) }; var rtb = new RichTextBox { Dock = DockStyle.Fill, Text = BuildDiffPreview(cs) }; var btn = new Button { Text = "Approve", Dock = DockStyle.Bottom }; btn.Click += (_, _) => { d.DialogResult = DialogResult.OK; d.Close(); }; d.Controls.Add(rtb); d.Controls.Add(btn); return d; }

        private static string BuildDiffPreview(FileChangeSet cs) { var sb = new StringBuilder(); foreach (var f in cs.Files) { sb.AppendLine($"--- {f.RelativePath} ---"); sb.AppendLine(f.ProposedContent); sb.AppendLine(); } return sb.ToString(); }

        private async Task<string> ApplyAndDeployChangeSetAsync(FileChangeSet cs)
        {
            foreach (var f in cs.Files) { var path = Path.Combine(_selectedRepoPath!, f.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, f.ProposedContent); }
            if (string.IsNullOrEmpty(_selectedOrgAlias)) return "Applied locally.";
            var result = await _salesforceCliService.DeployFilesToOrgAsync(_selectedRepoPath!, _selectedOrgAlias, cs.Files.Select(f => f.RelativePath).ToList());
            return result.ExitCode == 0 ? "Deployed successfully." : "Deployment failed.";
        }

        private async Task<string> DeployToOrgAsync()
        {
            if (string.IsNullOrEmpty(_selectedOrgAlias)) return "No org selected.";
            var result = await _salesforceCliService.DeployToOrgAsync(_selectedRepoPath!, _selectedOrgAlias);
            return result.ExitCode == 0 ? "Deployed successfully." : "Deployment failed.";
        }

        private void AppendToChat(string t, Color c) { if (rtbChat.InvokeRequired) { rtbChat.Invoke(() => AppendToChat(t, c)); return; } rtbChat.SelectionStart = rtbChat.TextLength; rtbChat.SelectionColor = c; rtbChat.AppendText(t + Environment.NewLine); rtbChat.ScrollToCaret(); }
        private JiraWorkItem? GetSelectedJiraStory() => dgvJiraStories.SelectedRows.Count > 0 ? dgvJiraStories.SelectedRows[0].DataBoundItem as JiraWorkItem : null;
        private JiraStoryFilter BuildJiraStoryFilter() => new JiraStoryFilter { SearchText = txtJiraSearch.Text, SpaceOrProject = txtJiraSpace.Text, IssueType = cmbJiraType.Text, Status = cmbJiraStatus.Text, Sprint = txtJiraSprint.Text };
    }
}

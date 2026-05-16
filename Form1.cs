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
        private const string PlaceholderText = "Type your command here... (e.g., 'Create an Account trigger' or 'Deploy to sandbox')";
        private const int BalanceCheckIntervalMs = 300000;

        private readonly string _apiKey = AiProviderSettings.ApiKey;
        private readonly DeepSeekClient _deepSeekClient;
        private readonly SalesforceCliService _salesforceCliService;
        private readonly SalesforceValidationService _salesforceValidationService;
        private readonly RepoContextService _repoContextService;
        private readonly CodeEditService _codeEditService;
        private readonly CommandApprovalService _commandApprovalService;
        private readonly ProfileFlsToolService _profileFlsToolService;
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
            _codeEditService = new CodeEditService(_repoContextService, _deepSeekClient, ReportProcessingStep);
            _commandApprovalService = new CommandApprovalService();
            _profileFlsToolService = new ProfileFlsToolService();
            _storyAnalyzerService = new StoryAnalyzerService(_deepSeekClient);
            _jiraService = new JiraService();
            _configMetadataOrchestrator = new ConfigMetadataOrchestrator(new IConfigWorkItemHandler[]
            {
                _codeEditService,
                new ObjectManagementService(),
                new ProfileManagementService(_profileFlsToolService),
                new PermissionSetManagementService(),
                new RecordTypeManagementService(),
                new LabelManagementService(),
                new CustomPermissionManagementService(),
                new CustomMetadataManagementService(),
                new GlobalValueSetManagementService(),
                new QuickActionManagementService(),
                new LayoutManagementService(),
                new FlexipageManagementService()
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
                ShowCoverageProgress(story, 4, "Checking for hidden layout, quick action, flow, or unsupported config work...");
            }

            normalizedPlan = RequirementCompletenessGuard.AddConservativeUnsupportedItems(storyText, normalizedPlan);

            if (showProgress)
            {
                ShowCoverageProgress(story, 5, "Assessing what can be handled automatically and what still needs manual work...");
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
            if (!System.IO.File.Exists(spinnerPath))
            {
                return string.Empty;
            }

            var bytes = System.IO.File.ReadAllBytes(spinnerPath);
            return $"data:image/gif;base64,{Convert.ToBase64String(bytes)}";
        }

        private static int GetThinkingSpinnerSizePx()
        {
            var rawSize = GetEnvironmentSetting("EZBERP_THINKING_GIF_SIZE", "44")
                .Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            return int.TryParse(rawSize, out var size) && size is >= 12 and <= 160
                ? size
                : 44;
        }

        private static string GetEnvironmentSetting(string name, string fallback)
        {
            return FirstNonBlankSetting(
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process),
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine),
                fallback);
        }

        private static string FirstNonBlankSetting(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
        }
        private static string BuildJiraCoverageCacheKey(string storyKey, string repoPath)
        {
            var normalizedRepoPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var visionMode = AiProviderSettings.UseOpenAiForInlineImages ? "vision-auto" : "text-only";
            const string coverageAnalyzerCacheVersion = "coverage-v15-ignore-struck-through-story-text";
            return $"{coverageAnalyzerCacheVersion}|{visionMode}|{normalizedRepoPath}|{storyKey}";
        }

        private static string BuildSimpleHtml(string title, string body)
        {
            return $$"""
<!doctype html>
<html><head><meta charset="utf-8"><style>
body { font-family: Segoe UI, Arial, sans-serif; background:#f5f7fb; color:#1f2933; margin:0; padding:24px; }
.card { background:white; border:1px solid #d9e2ec; border-radius:10px; padding:20px; box-shadow:0 8px 20px rgba(15,23,42,.08); }
h1 { margin-top:0; font-size:20px; color:#102a43; }
</style></head><body><div class="card"><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{body}}</p></div></body></html>
""";
        }

        private static string BuildImageReadingDiagnosticHtml(JiraWorkItem story, IReadOnlyList<JiraStoryAnalysisBlock> imageBlocks, string diagnostic)
        {
            var imageRows = imageBlocks.Count == 0
                ? "<li>No image blocks were found in the analysis payload.</li>"
                : string.Join(Environment.NewLine, imageBlocks.Select(block =>
                    $"<li><strong>{WebUtility.HtmlEncode(block.FileName)}</strong><br><span>{WebUtility.HtmlEncode(block.MimeType)}</span><br><code>{WebUtility.HtmlEncode(block.LocalPath)}</code></li>"));

            return $$"""
<!doctype html>
<html><head><meta charset="utf-8"><style>
body { font-family: Segoe UI, Arial, sans-serif; background:#f5f7fb; color:#1f2933; margin:0; padding:24px; }
.card { background:white; border:1px solid #d9e2ec; border-radius:10px; padding:20px; box-shadow:0 8px 20px rgba(15,23,42,.08); }
h1 { margin-top:0; font-size:20px; color:#102a43; }
h2 { font-size:15px; margin-top:18px; color:#102a43; }
.pill { display:inline-block; background:#e6f0ff; color:#174ea6; border-radius:999px; padding:5px 10px; font-size:12px; font-weight:600; }
li { margin:10px 0; }
code { color:#52606d; font-size:12px; word-break:break-all; }
pre { white-space:pre-wrap; background:#0f172a; color:#e2e8f0; border-radius:8px; padding:14px; font-family:Consolas, monospace; }
</style></head><body><div class="card">
<h1>{{WebUtility.HtmlEncode(story.Key)}} - image reading diagnostic</h1>
<div class="pill">{{imageBlocks.Count}} inline image block(s) sent/available for vision analysis</div>
<h2>Images in AI payload</h2>
<ul>{{imageRows}}</ul>
<h2>Vision model response</h2>
<pre>{{WebUtility.HtmlEncode(diagnostic)}}</pre>
</div></body></html>
""";
        }

        private static string BuildCoverageHtml(JiraWorkItem story, SalesforceConfigCoverage coverage)
        {
            var supported = coverage.Results.Where(result => result.IsSupported).ToList();
            var unsupported = coverage.Results.Where(result => !result.IsSupported).ToList();
            var alternatives = coverage.Results.Where(result => !result.IsSupported && result.AlternativeRequirement is not null).ToList();
            var accent = coverage.UnsupportedRequirements == 0 ? "#138a36" : "#b7791f";

            var builder = new StringBuilder();
            var thinkingSpinnerSizePx = GetThinkingSpinnerSizePx();
            builder.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><style>");
            builder.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;background:#f5f7fb;color:#1f2933;margin:0;padding:22px}.card{background:white;border:1px solid #d9e2ec;border-radius:12px;padding:20px;box-shadow:0 8px 20px rgba(15,23,42,.08)}h1{font-size:20px;margin:0 0 6px;color:#102a43}.sub{color:#52606d;margin-bottom:18px}.score{display:inline-block;background:" + accent + ";color:white;border-radius:999px;padding:8px 14px;font-weight:700}.grid{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin-top:18px}.panel{border:1px solid #d9e2ec;border-radius:10px;padding:14px;background:#fbfdff}.panel h2{font-size:15px;margin:0 0 10px}.item{padding:10px 0;border-top:1px solid #edf2f7}.item:first-of-type{border-top:0}.reason{color:#52606d;font-size:12px;margin-top:4px}.ok{color:#138a36}.no{color:#b42318}.alt{color:#8a5a00}.altbox{grid-column:1 / -1;background:#fffaf0;border-color:#f6c453}");
            builder.AppendLine("</style></head><body><div class=\"card\">");
            builder.AppendLine($"<h1>{WebUtility.HtmlEncode(story.Key)} - {WebUtility.HtmlEncode(story.Summary)}</h1>");
            builder.AppendLine($"<div class=\"sub\">AI coverage preview only. No files are changed from this action.</div><span class=\"score\">{coverage.SupportedRequirements} of {coverage.TotalRequirements} supported ({coverage.CoveragePercentage}%)</span>");
            builder.AppendLine("<div class=\"grid\"><div class=\"panel\"><h2 class=\"ok\">Can do</h2>");
            AppendCoverageItems(builder, supported);
            builder.AppendLine("</div><div class=\"panel\"><h2 class=\"no\">Cannot do yet</h2>");
            AppendCoverageItems(builder, unsupported);
            builder.AppendLine("</div><div class=\"panel altbox\"><h2 class=\"alt\">Alternative available</h2>");
            AppendAlternativeCoverageItems(builder, alternatives);
            builder.AppendLine("</div></div></div></body></html>");
            return builder.ToString();
        }

        private static void AppendCoverageItems(StringBuilder builder, IReadOnlyList<RequirementCoverageResult> results)
        {
            if (results.Count == 0)
            {
                builder.AppendLine("<div class=\"item\">None</div>");
                return;
            }

            foreach (var result in results)
            {
                var requirement = result.Requirement;
                var headline = SalesforceConfigPlanFormatter.BuildRequirementHeadline(requirement);
                var detail = SalesforceConfigPlanFormatter.BuildRequirementDetail(requirement);
                builder.AppendLine("<div class=\"item\">");
                builder.AppendLine($"<strong>{WebUtility.HtmlEncode(headline)}</strong>");

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    builder.AppendLine($"<div class=\"reason\">{WebUtility.HtmlEncode(detail)}</div>");
                }

                builder.AppendLine($"<div class=\"reason\">{WebUtility.HtmlEncode(result.Reason)}</div>");
                builder.AppendLine("</div>");
            }
        }
        private static void AppendAlternativeCoverageItems(StringBuilder builder, IReadOnlyList<RequirementCoverageResult> results)
        {
            if (results.Count == 0)
            {
                builder.AppendLine("<div class=\"item\">None</div>");
                return;
            }

            foreach (var result in results)
            {
                var alternative = result.AlternativeRequirement!;
                var headline = SalesforceConfigPlanFormatter.BuildRequirementHeadline(alternative);
                var detail = SalesforceConfigPlanFormatter.BuildRequirementDetail(alternative);
                builder.AppendLine("<div class=\"item\">");
                builder.AppendLine($"<strong>{WebUtility.HtmlEncode(headline)}</strong>");

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    builder.AppendLine($"<div class=\"reason\">{WebUtility.HtmlEncode(detail)}</div>");
                }

                if (!string.IsNullOrWhiteSpace(result.AlternativeReason))
                {
                    builder.AppendLine($"<div class=\"reason\">{WebUtility.HtmlEncode(result.AlternativeReason)}</div>");
                }

                builder.AppendLine("</div>");
            }
        }
        private static string FirstNonBlank(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
        }
        private void FixJiraGridRows(int rowIndex, int rowCount)
        {
            for (var index = rowIndex; index < rowIndex + rowCount && index < dgvJiraStories.Rows.Count; index++)
            {
                dgvJiraStories.Rows[index].Height = 24;
                dgvJiraStories.Rows[index].Resizable = DataGridViewTriState.False;
            }
        }
        private void AddJiraColumn(string headerText, string dataPropertyName, int width)
        {
            dgvJiraStories.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = headerText,
                DataPropertyName = dataPropertyName,
                Width = width
            });
        }
        private void ConfigureCommandInput()
        {
            // Keep a hidden input instance for legacy command-processing code paths, but do not render it.
            txtCommandInput = new TextBox
            {
                Location = Point.Empty,
                Size = Size.Empty,
                Multiline = true,
                Font = new Font("Consolas", 10),
                BackColor = Color.White,
                ForeColor = Color.Gray,
                BorderStyle = BorderStyle.None,
                Text = PlaceholderText,
                Visible = false,
                Enabled = false
            };
        }

        private void ConfigureSendButton()
        {
            btnSend.Location = Point.Empty;
            btnSend.Size = Size.Empty;
            btnSend.Visible = false;
            btnSend.Enabled = false;
        }

        private void ConfigureChatArea()
        {
            rtbChat.Location = new Point(24, 360);
            rtbChat.Size = new Size(1136, 300);
            rtbChat.Font = new Font("Consolas", 9);
            rtbChat.BackColor = Color.FromArgb(30, 30, 30);
            rtbChat.ForeColor = Color.FromArgb(220, 220, 220);
            rtbChat.BorderStyle = BorderStyle.None;
            rtbChat.ReadOnly = true;
        }

        private void ConfigureLabels()
        {
            lblRemainingBalance.ForeColor = Color.Gold;
            lblRemainingBalance.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            lblSelectedOrg.ForeColor = Color.LightGray;
            lblSelectedRepo.ForeColor = Color.LightGray;
            lblProcessing.ForeColor = Color.LightSkyBlue;
            lblSelectedRepo.Location = new Point(24, 684);
            lblProcessing.AutoSize = false;
            lblProcessing.Location = new Point(770, 684);
            lblProcessing.TextAlign = ContentAlignment.MiddleLeft;
            lblProcessing.UseMnemonic = false;

            InitializeLoadingImage();
            AlignProcessingIndicator();
            InitializeAiLogo();
            picAiLogo.Location = new Point(430, 20);
            lblAiProvider.Location = new Point(470, 28);
            lblRemainingBalance.Location = new Point(920, 8);
            lblSelectedOrg.Location = new Point(920, 30);
        }

        private void InitializeAiLogo()
        {
            var isOpenAi = AiProviderSettings.Provider == AiProvider.OpenAI;
            var pathVar = isOpenAi ? "OPENAI_LOGO_PATH" : "DEEPSEEK_LOGO_PATH";
            var path = GetEnvironmentSetting(pathVar, string.Empty);
            var sizePx = GetAiLogoSizePx();

            lblAiProvider.Text = isOpenAi ? "Powered by OpenAI" : "Powered by DeepSeek";
            picAiLogo.Size = new Size(sizePx, sizePx);
            picAiLogo.SizeMode = PictureBoxSizeMode.Zoom;

            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                try
                {
                    picAiLogo.Image = Image.FromFile(path);
                }
                catch
                {
                    // Fallback to nothing if image fails to load
                }
            }
        }

        private static int GetAiLogoSizePx()
        {
            var rawSize = GetEnvironmentSetting("LOG_SIZE", "32")
                .Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            return int.TryParse(rawSize, out var size) && size is >= 12 and <= 160
                ? size
                : 32;
        }

        private void InitializeLoadingImage()
        {
            var path = GetEnvironmentSetting("EZBERP_LOADING_GIF_PATH", string.Empty);
            var sizePx = GetLoadingImageSizePx();

            picLoading.Size = new Size(sizePx, sizePx);
            picLoading.SizeMode = PictureBoxSizeMode.Zoom;
            picLoading.Visible = false;

            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                try
                {
                    picLoading.Image = Image.FromFile(path);
                }
                catch
                {
                    // Fallback to nothing if image fails to load
                }
            }
        }

        private static int GetLoadingImageSizePx()
        {
            var rawSize = GetEnvironmentSetting("EZBERP_LOADING_GIF_SIZE", "44")
                .Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            return int.TryParse(rawSize, out var size) && size is >= 12 and <= 160
                ? size
                : 44;
        }

        private void ConfigureButtons()
        {
            StyleButton(btnSelectOrg);
            StyleButton(btnSelectRepo);
            StyleButton(btnReviewGitChanges);
            StyleButton(btnLoadJiraStories);
            StyleButton(btnProcessJiraStory);
        }

        private void ConfigureForm()
        {
            Text = "Salesforce AI - Assistant [Config Only - No Apex/Aura/LWC/VF Pages] - Beta Version 1.0";
            Size = new Size(1210, 760);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(45, 45, 48);
            Resize += (_, _) => AlignProcessingIndicator();
        }

        private void WireEvents()
        {
            btnSelectOrg.Click += BtnSelectOrg_Click!;
            btnSelectRepo.Click += BtnSelectRepo_Click!;
            btnReviewGitChanges.Click += BtnReviewGitChanges_Click!;
            btnLoadJiraStories.Click += BtnLoadJiraStories_Click!;
            btnProcessJiraStory.Click += BtnProcessJiraStory_Click!;
        }

        private void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
            button.BackColor = Color.FromArgb(60, 60, 65);
            button.ForeColor = Color.White;
        }

        private void SetProcessingState(bool isProcessing, string message = "Processing request...")
        {
            picLoading.Visible = isProcessing;
            lblProcessing.Visible = isProcessing;
            lblProcessing.Text = message;
            AlignProcessingIndicator();

            btnSend.Enabled = false;
            txtCommandInput.Enabled = false;
            btnSelectOrg.Enabled = !isProcessing;
            btnSelectRepo.Enabled = !isProcessing;
            btnReviewGitChanges.Enabled = !isProcessing && !string.IsNullOrWhiteSpace(_selectedRepoPath);
            btnLoadJiraStories.Enabled = !isProcessing;
            btnProcessJiraStory.Enabled = !isProcessing && dgvJiraStories.Rows.Count > 0;

            // Disable filter controls
            txtJiraSearch.Enabled = !isProcessing;
            txtJiraSpace.Enabled = !isProcessing;
            txtJiraSprint.Enabled = !isProcessing;
            cmbJiraType.Enabled = !isProcessing;
            cmbJiraStatus.Enabled = !isProcessing;
            cmbJiraLeadConsultant.Enabled = !isProcessing;
            dgvJiraStories.Enabled = !isProcessing;

            ForceJiraFilterColors();
        }

        private void ReportProcessingStep(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ReportProcessingStep(message)));
                return;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            lblProcessing.Visible = true;
            lblProcessing.Text = message;
            AlignProcessingIndicator();
            AppendToChat(message, Color.LightBlue);
        }

        private void AlignProcessingIndicator()
        {
            if (picLoading is null || lblProcessing is null)
            {
                return;
            }

            const int bottomMargin = 1;
            const int rightMargin = 24;
            const int gap = 8;
            const int messageWidth = 420;

            var iconSize = picLoading.Width > 0 ? picLoading.Width : GetLoadingImageSizePx();
            var y = Math.Max(0, ClientSize.Height - iconSize - bottomMargin);
            var labelX = Math.Max(24, ClientSize.Width - rightMargin - messageWidth);
            var iconX = Math.Max(24, labelX - gap - iconSize);

            picLoading.Location = new Point(iconX, y);
            lblProcessing.Location = new Point(labelX, y);
            lblProcessing.Size = new Size(Math.Max(120, ClientSize.Width - labelX - rightMargin), iconSize);
        }

        private void ClearPlaceholderIfNeeded()
        {
            if (txtCommandInput.Text != PlaceholderText)
            {
                return;
            }

            txtCommandInput.Text = string.Empty;
            txtCommandInput.ForeColor = Color.White;
        }

        private void RestorePlaceholderIfNeeded()
        {
            if (!string.IsNullOrWhiteSpace(txtCommandInput.Text))
            {
                return;
            }

            txtCommandInput.Text = PlaceholderText;
            txtCommandInput.ForeColor = Color.Gray;
        }

        private void InitializeEngine()
        {
            var raw = AiProviderSettings.GetSetting("AI_PROVIDER","");
            var source = AiProviderSettings.GetSettingSource("AI_PROVIDER");
            if (source == "None/Fallback") source = AiProviderSettings.GetSettingSource("AI_PROVIDER");

            AppendToChat($"AI Engine initialized: {AiProviderSettings.ProviderDisplayName} (Value: '{raw}', Source: {source})", Color.Gray);

            _balanceCheckTimer = new System.Windows.Forms.Timer
            {
                Interval = BalanceCheckIntervalMs
            };
            _balanceCheckTimer.Tick += async (_, _) => await CheckRemainingBalance();
            _balanceCheckTimer.Start();
            _ = CheckRemainingBalance();
        }

        private async void BtnSelectOrg_Click(object sender, EventArgs e)
        {
            SetProcessingState(true, "Loading available orgs...");

            try
            {
                AppendToChat("Loading available orgs...", Color.Gray);
                var orgs = await _salesforceCliService.GetOrgListAsync();
                if (orgs.Count == 0)
                {
                    MessageBox.Show("No orgs found. Please login using 'sf login' command in terminal first.",
                        "No Orgs Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using var orgDialog = CreateOrgSelectionDialog(orgs);
                if (orgDialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(_selectedOrgAlias))
                {
                    AppendToChat($"Target org set to: {_selectedOrgAlias}", Color.LightGreen);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading orgs: {ex.Message}", "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                SetProcessingState(false);
                if (txtCommandInput.Visible) txtCommandInput.Focus();
            }
        }


        private Form CreateOrgSelectionDialog(IReadOnlyList<OrgInfo> orgs)
        {
            var orgDialog = new Form
            {
                Text = "Select Target Org",
                Size = new Size(500, 400),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };

            var listBox = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10),
                BackColor = Color.FromArgb(35, 35, 35),
                ForeColor = Color.White
            };

            foreach (var org in orgs)
            {
                listBox.Items.Add(org.ToString());
            }

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var selectBtn = new Button
            {
                Text = "Select",
                Dock = DockStyle.Right,
                Width = 100,
                Height = 40,
                Margin = new Padding(5),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Dock = DockStyle.Right,
                Width = 100,
                Height = 40,
                Margin = new Padding(5),
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            selectBtn.Click += (_, _) =>
            {
                if (listBox.SelectedIndex < 0)
                {
                    return;
                }

                _selectedOrgAlias = orgs[listBox.SelectedIndex].Alias;
                lblSelectedOrg.Text = $"Selected Org: {_selectedOrgAlias}";
                lblSelectedOrg.ForeColor = Color.LightGreen;
                orgDialog.DialogResult = DialogResult.OK;
                orgDialog.Close();
            };

            cancelBtn.Click += (_, _) => orgDialog.Close();

            buttonPanel.Controls.Add(selectBtn);
            buttonPanel.Controls.Add(cancelBtn);
            orgDialog.Controls.Add(listBox);
            orgDialog.Controls.Add(buttonPanel);

            return orgDialog;
        }


        private async void BtnLoadJiraStories_Click(object sender, EventArgs e)
        {
            SetProcessingState(true, "Loading Jira stories...");

            try
            {
                AppendToChat("Preparing Jira search request...", Color.Gray);
                var stories = await _jiraService.SearchStoriesAsync(BuildJiraStoryFilter());
                AppendToChat($"Jira JQL: {_jiraService.LastSearchJql}", Color.Gray);
                AppendToChat($"Jira returned {_jiraService.LastSearchResultCount} story row(s).", Color.Gray);

                _jiraStories.Clear();
                foreach (var story in stories)
                {
                    _jiraStories.Add(story);
                }

                lblJiraStatus.Text = $"Jira stories: {stories.Count} loaded";
                AppendToChat($"Loaded {stories.Count} Jira stories.", Color.LightGreen);
            }
            catch (Exception ex)
            {
                lblJiraStatus.Text = "Jira stories: load failed";
                AppendToChat($"Jira Error: {ex.Message}", Color.Red);
            }
            finally
            {
                SetProcessingState(false);
            }
        }

        private async void BtnProcessJiraStory_Click(object sender, EventArgs e)
        {
            var selected = GetSelectedJiraStory();
            if (selected is null)
            {
                ShowProcessSelectionMessage("Please select one Jira story first.");
                return;
            }

            if (!ValidateProcessSelectedPrerequisites())
            {
                return;
            }

            if (!ValidateSelectedStoryHasCoverageAnalysis(selected))
            {
                return;
            }

            SetProcessingState(true, $"Loading {selected.Key} from Jira...");

            try
            {
                AppendToChat($"\nJira: {selected.Key} - {selected.Summary}", Color.Cyan);
                ReportProcessingStep($"Loading {selected.Key} story text and cached coverage...");
                var response = await ProcessSelectedJiraStoryAsync(selected);
                AppendToChat($"AI: {response}", Color.White);
                await CheckRemainingBalance();
            }
            catch (Exception ex)
            {
                AppendToChat($"Error: {ex.Message}", Color.Red);
            }
            finally
            {
                SetProcessingState(false);
            }
        }

        private bool ValidateProcessSelectedPrerequisites()
        {
            var missingItems = new List<string>();
            if (string.IsNullOrWhiteSpace(_selectedRepoPath))
            {
                missingItems.Add("Salesforce repo");
            }

            if (string.IsNullOrWhiteSpace(_selectedOrgAlias))
            {
                missingItems.Add("target org");
            }

            if (missingItems.Count == 0)
            {
                return true;
            }

            var missingText = missingItems.Count == 1
                ? missingItems[0]
                : string.Join(" and ", missingItems);

            ShowProcessSelectionMessage($"Please select your {missingText} before processing the selected Jira story.");
            return false;
        }

        private bool ValidateSelectedStoryHasCoverageAnalysis(JiraWorkItem selected)
        {
            if (TryGetCachedJiraCoverageAnalysis(selected, out _))
            {
                return true;
            }

            ShowProcessSelectionMessage(
                $"Please analyze coverage for {selected.Key} first, then press Process Selected.");
            return false;
        }

        private void ShowProcessSelectionMessage(string message)
        {
            AppendToChat(message, Color.Orange);
            MessageBox.Show(message, "Selection required", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private JiraStoryFilter BuildJiraStoryFilter()
        {
            return new JiraStoryFilter
            {
                SearchText = txtJiraSearch.Text.Trim().Equals("Search work", StringComparison.OrdinalIgnoreCase) ? string.Empty : txtJiraSearch.Text.Trim(),
                SpaceOrProject = txtJiraSpace.Text.Trim(),
                IssueType = cmbJiraType.Text.Trim(),
                Status = cmbJiraStatus.Text.Trim(),
                LeadConsultant = cmbJiraLeadConsultant.Text.Trim(),
                Sprint = txtJiraSprint.Text.Trim()
            };
        }

        private JiraWorkItem? GetSelectedJiraStory()
        {
            if (dgvJiraStories.SelectedRows.Count == 0)
            {
                return null;
            }

            return dgvJiraStories.SelectedRows[0].DataBoundItem as JiraWorkItem;
        }
        private async Task<string> ProcessSelectedJiraStoryAsync(JiraWorkItem selected)
        {
            if (TryGetCachedJiraCoverageAnalysis(selected, out var cachedAnalysis))
            {
                AppendToChat($"Using cached coverage analysis for {selected.Key}.", Color.Gray);
                ReportProcessingStep($"Using cached coverage analysis for {selected.Key}...");
                var cachedStoryText = cachedAnalysis.StoryText;
                if (string.IsNullOrWhiteSpace(cachedStoryText))
                {
                    ReportProcessingStep($"Cached coverage did not include story text. Retrieving {selected.Key} details...");
                    cachedStoryText = await _jiraService.GetStoryAnalysisTextAsync(selected.Key);
                }

                return await ProcessSalesforceConfigCoverageAsync(cachedAnalysis.Coverage, cachedStoryText);
            }

            ReportProcessingStep($"Retrieving full Jira story text for {selected.Key}...");
            var storyText = await _jiraService.GetStoryAnalysisTextAsync(selected.Key);
            return await ProcessWithDeepSeekAsync(storyText);
        }

        private void BtnReviewGitChanges_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_selectedRepoPath))
            {
                MessageBox.Show("Please select your Salesforce repo first.", "Repository required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var gitReviewForm = new GitReviewForm(_selectedRepoPath);
            gitReviewForm.ShowDialog(this);
        }

        private async void BtnSelectRepo_Click(object sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select your Salesforce project repository",
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            _selectedRepoPath = dialog.SelectedPath;
            var isValid = await Task.Run(() => _repoContextService.ValidateSalesforceProject(_selectedRepoPath));
            if (!isValid)
            {
                MessageBox.Show("Selected folder does not appear to be a valid Salesforce project (missing force-app/ or src/ folder).",
                    "Invalid Repository", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            lblSelectedRepo.Text = $"Selected Repo: {_selectedRepoPath}";
            lblSelectedRepo.ForeColor = Color.LightGreen;
            btnReviewGitChanges.Enabled = true;
            AppendToChat($"Repository set to: {_selectedRepoPath}", Color.LightGreen);

            var stats = await Task.Run(() => _repoContextService.GetRepoStats(_selectedRepoPath));
            AppendToChat($"Found {stats.Classes} Apex classes, {stats.Triggers} triggers, {stats.Lwc} LWC components", Color.Gray);
        }

        private string GetRepoStatsSummary()
        {
            return string.IsNullOrWhiteSpace(_selectedRepoPath)
                ? "No repository selected"
                : _repoContextService.BuildRepoStatsSummary(_selectedRepoPath);
        }

        private async Task CheckRemainingBalance()
        {
            if (AiProviderSettings.Provider == AiProvider.OpenAI)
            {
                lblRemainingBalance.Visible = false;
                lblRemainingBalance.Text = string.Empty;
                return;
            }

            lblRemainingBalance.Visible = true;
            var balance = await _deepSeekClient.GetBalanceAsync();
            lblRemainingBalance.Text = balance.Text;
            lblRemainingBalance.ForeColor = balance.IsError
                ? Color.Red
                : balance.IsWarning
                    ? Color.OrangeRed
                    : balance.Text.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) || balance.Text.Contains("Error", StringComparison.OrdinalIgnoreCase) || balance.Text.Contains("parse", StringComparison.OrdinalIgnoreCase)
                        ? Color.Gray
                        : Color.Gold;
        }

        private async void BtnSend_Click(object sender, EventArgs e)
        {
            var userCommand = txtCommandInput.Text.Trim();
            if (!ValidateSendRequest(userCommand))
            {
                return;
            }

            ResetCommandInput();
            SetProcessingState(true);
            AppendToChat($"\nYou: {userCommand}", Color.Cyan);

            try
            {
                var response = await ProcessWithDeepSeekAsync(userCommand);
                AppendToChat($"AI: {response}", Color.White);
                await CheckRemainingBalance();
            }
            catch (Exception ex)
            {
                AppendToChat($"Error: {ex.Message}", Color.Red);
            }
            finally
            {
                SetProcessingState(false);
                if (txtCommandInput.Visible) txtCommandInput.Focus();
            }
        }

        private bool ValidateSendRequest(string userCommand)
        {
            if (string.IsNullOrWhiteSpace(userCommand) || userCommand == PlaceholderText)
            {
                AppendToChat("Please enter a command", Color.Orange);
                return false;
            }

            if (string.IsNullOrWhiteSpace(_selectedRepoPath))
            {
                AppendToChat("No repository selected. Please select your Salesforce repo first.", Color.Red);
                return false;
            }

            if (string.IsNullOrWhiteSpace(_selectedOrgAlias))
            {
                AppendToChat("No Salesforce org selected. Please select your target org first.", Color.Red);
                return false;
            }

            return true;
        }

        private void ResetCommandInput()
        {
            txtCommandInput.Clear();
            txtCommandInput.Text = PlaceholderText;
            txtCommandInput.ForeColor = Color.Gray;
        }

        private async Task<string> ProcessWithDeepSeekAsync(string userCommand)
        {
            if (userCommand.Contains("deploy", StringComparison.OrdinalIgnoreCase))
            {
                return await DeployToOrgAsync();
            }

            if (IsInformationalQuestion(userCommand))
            {
                return await AnswerInformationQuestionAsync(userCommand);
            }

            if (_storyAnalyzerService.IsSalesforceConfigRequest(userCommand))
            {
                ReportProcessingStep("Extracting Salesforce requirements with AI...");
                var configPlan = await _storyAnalyzerService.AnalyzeAsync(_selectedRepoPath!, userCommand);
                ReportProcessingStep("Normalizing extracted requirements...");
                var normalizedPlan = _configMetadataOrchestrator.NormalizePlan(configPlan);
                ReportProcessingStep("Checking for hidden unsupported config work...");
                normalizedPlan = RequirementCompletenessGuard.AddConservativeUnsupportedItems(userCommand, normalizedPlan);
                if (normalizedPlan.Requirements.Count == 0)
                {
                    return "No actionable Salesforce config changes were extracted, so no files were changed.";
                }

                ReportProcessingStep("Assessing supported work and possible alternatives...");
                var coverage = await _configMetadataOrchestrator.AssessCoverageAsync(_selectedRepoPath!, normalizedPlan);
                return await ProcessSalesforceConfigCoverageAsync(coverage);
            }

            ReportProcessingStep("Indexing Salesforce repo files for code context...");
            var repoFiles = await Task.Run(() => _repoContextService.GetAllSalesforceFiles(_selectedRepoPath!));
            ReportProcessingStep("Reading relevant repository context...");
            var readFiles = await BuildAutomaticReadFilesContextAsync(userCommand);
            var systemPrompt = BuildSystemPrompt(userCommand, repoFiles, readFiles);
            ReportProcessingStep("Asking AI for an implementation plan...");
            var currentResponse = CleanupGeneratedResponse(
                await _deepSeekClient.SendChatAsync(DeepSeekModels.Coding, systemPrompt, userCommand, 0.3, 4000)
            );

            const int maxIterations = 10;
            var iteration = 0;

            while (iteration < maxIterations)
            {
                var fileToRead = ExtractFileReadRequest(currentResponse);
                if (string.IsNullOrEmpty(fileToRead))
                {
                    break;
                }

                var fileContent = await _repoContextService.ReadFileFromRepoAsync(_selectedRepoPath!, fileToRead);
                if (string.IsNullOrEmpty(fileContent))
                {
                    currentResponse = $"Could not find the requested file: {fileToRead}\n\n{currentResponse}";
                    break;
                }

                readFiles[fileToRead] = fileContent;
                var allFilesContext = BuildReadFilesContext(readFiles);
                var followUpPrompt = $@"I have read the following files:

{allFilesContext}

Based on the patterns in these files, now please complete the original request: {userCommand}

Return only the minimal required file changes using one or more <write_file path=""relative/path"">...</write_file> blocks. Do not claim files were already written. If you need another file, ask using <read_file>relative/path</read_file>.";

                currentResponse = CleanupGeneratedResponse(
                    await _deepSeekClient.SendChatAsync(
                        DeepSeekModels.Coding,
                        "You are a Salesforce assistant. Use only the provided file contents. Return minimal write_file blocks or read_file requests.",
                        followUpPrompt,
                        0.5,
                        4000)
                );

                iteration++;
            }

            var writeRequests = ExtractFileWrites(currentResponse);
            var surgicalEdits = ExtractSurgicalEdits(currentResponse);
            var profileFlsRequest = ExtractProfileFlsRequest(currentResponse)
                ?? TryInferProfileFlsRequest(userCommand, writeRequests);

            if (profileFlsRequest is not null)
            {
                profileFlsRequest = RefineProfileFlsRequestFromUserCommand(userCommand, profileFlsRequest);
            }

            if (profileFlsRequest is not null)
            {
                writeRequests = writeRequests
                    .Where(write => !IsProfileMetadataPath(write.RelativePath))
                    .ToList();
            }

            if (writeRequests.Count > 0 || surgicalEdits.Count > 0 || profileFlsRequest is not null)
            {
                var changeSets = new List<FileChangeSet>();

                if (writeRequests.Count > 0)
                {
                    changeSets.Add(await _codeEditService.BuildDirectWriteChangeSetAsync(_selectedRepoPath!, writeRequests));
                }

                if (surgicalEdits.Count > 0)
                {
                    changeSets.Add(await _codeEditService.BuildSurgicalChangeSetAsync(_selectedRepoPath!, surgicalEdits));
                }

                if (profileFlsRequest is not null)
                {
                    changeSets.Add(await _profileFlsToolService.BuildChangeSetAsync(_selectedRepoPath!, profileFlsRequest));
                }

                var changeSet = CombineChangeSets("AI proposed metadata changes", changeSets);
                if (!ApproveChangeSet(changeSet))
                {
                    return "File changes were not applied because approval was declined.";
                }

                var deployStatus = await ApplyAndDeployChangeSetAsync(changeSet);
                var cleanResponse = Regex.Replace(currentResponse,
                    @"<write_(?:to_)?file\s+path=[""'][^""']+[""']>.*?</write_(?:to_)?file>",
                    "[FILE WRITTEN]",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                cleanResponse = Regex.Replace(cleanResponse,
                    @"<profile_fls\b[^>]*\/?>",
                    "[PROFILE FLS UPDATED]",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                return $"Approved and applied {changeSet.Files.Count} file change(s){deployStatus}.\n\n{cleanResponse}";
            }

            if (ShouldFallbackToGeneratedCode(userCommand, currentResponse))
            {
                var changeSet = await _codeEditService.BuildGeneratedCodeChangeSetAsync(_selectedRepoPath!, currentResponse, userCommand);
                if (!ApproveChangeSet(changeSet))
                {
                    return "Generated code was not saved because approval was declined.";
                }

                var deployStatus = await ApplyAndDeployChangeSetAsync(changeSet);
                var appliedFiles = string.Join(", ", changeSet.Files.Select(file => file.RelativePath));
                return $"Approved and saved: {appliedFiles}{deployStatus}\n\n{currentResponse}";
            }

            if (LooksLikeToolNarration(currentResponse))
            {
                return "The model did not return a valid file patch, so no files were changed.\n\n" + currentResponse;
            }

            return currentResponse;
        }

        private async Task<string> ProcessSalesforceConfigCoverageAsync(SalesforceConfigCoverage coverage, string storyText = "")
        {
            if (!ApproveConfigCoverage(coverage))
            {
                return "Salesforce config changes were not proposed because coverage approval was declined.";
            }

            EnrichExecutableRequirementsWithStoryText(coverage, storyText);
            var executablePlan = BuildExecutableCoveragePlan(coverage, _includeCoverageAlternatives);

            if (executablePlan.Requirements.Count == 0)
            {
                return "No supported Salesforce config requirements or approved alternatives were found, so no files were changed.";
            }

            ReportProcessingStep($"Reviewing executable roadmap with {executablePlan.Requirements.Count} requirement(s)...");
            if (!ApproveConfigPlan(executablePlan))
            {
                return "Salesforce config roadmap was not applied because approval was declined.";
            }

            ReportProcessingStep("Building proposed file changes...");
            var configChangeSet = await _configMetadataOrchestrator.BuildChangeSetAsync(_selectedRepoPath!, executablePlan);
            if (configChangeSet.Files.Count == 0)
            {
                if (configChangeSet.Messages != null && configChangeSet.Messages.Count > 0)
                {
                    return string.Join(" | ", configChangeSet.Messages);
                }
                return "No Salesforce config file changes were generated.";
            }

            ReportProcessingStep($"Reviewing {configChangeSet.Files.Count} proposed file change(s)...");
            if (!ApproveChangeSet(configChangeSet))
            {
                return "Salesforce config file changes were not applied because approval was declined.";
            }

            var deployStatus = await ApplyAndDeployChangeSetAsync(configChangeSet);
            var appliedFiles = string.Join(", ", configChangeSet.Files.Select(file => file.RelativePath));
            return $"Approved and applied Salesforce config changes: {appliedFiles}{deployStatus}";
        }

        private static void EnrichExecutableRequirementsWithStoryText(SalesforceConfigCoverage coverage, string storyText)
        {
            if (string.IsNullOrWhiteSpace(storyText))
            {
                return;
            }

            foreach (var requirement in coverage.SupportedPlan.Requirements.Concat(coverage.AlternativePlan.Requirements))
            {
                if (!ShouldEnrichRequirementWithStoryText(requirement))
                {
                    continue;
                }

                const string marker = "Full Jira story context:";
                if (requirement.Description.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                requirement.Description = string.Join(Environment.NewLine + Environment.NewLine, new[]
                {
                    requirement.Description,
                    marker,
                    storyText
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }

        private static bool ShouldEnrichRequirementWithStoryText(SalesforceConfigRequirement requirement)
        {
            return string.Equals(requirement.Type, "implementation_code", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(requirement.Service, nameof(CodeEditService), StringComparison.OrdinalIgnoreCase);
        }

        private static SalesforceConfigPlan BuildExecutableCoveragePlan(SalesforceConfigCoverage coverage, bool includeAlternatives)
        {
            var plan = new SalesforceConfigPlan
            {
                Summary = coverage.OriginalPlan.Summary,
                Questions = coverage.OriginalPlan.Questions.ToList()
            };

            plan.Requirements.AddRange(coverage.SupportedPlan.Requirements);
            if (includeAlternatives)
            {
                plan.Requirements.AddRange(coverage.AlternativePlan.Requirements);
            }

            plan.Requirements = plan.Requirements
                .GroupBy(BuildExecutableRequirementKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            return plan;
        }

        private static string BuildExecutableRequirementKey(SalesforceConfigRequirement requirement)
        {
            return string.Join("|", new[]
            {
                requirement.Type,
                requirement.Service,
                requirement.Operation,
                requirement.ObjectApiName,
                requirement.FieldApiName,
                requirement.TargetMetadataName,
                requirement.ValidationRuleName,
                requirement.SuggestedTriggerEvent,
                requirement.SuggestedHelperMethodName
            }.Select(value => value?.Trim() ?? string.Empty));
        }

        private bool ApproveConfigCoverage(SalesforceConfigCoverage coverage)
        {
            _includeCoverageAlternatives = false;
            using var dialog = CreateConfigCoverageApprovalDialog(coverage);
            return dialog.ShowDialog() == DialogResult.OK;
        }

        private static bool IsCompleteSupportedCoverage(SalesforceConfigCoverage coverage)
        {
            return coverage.UnsupportedRequirements == 0;
        }

        private static bool IsCompleteCoverageWithAlternatives(SalesforceConfigCoverage coverage)
        {
            return coverage.AlternativeRequirements > 0
                   && coverage.SupportedRequirements + coverage.AlternativeRequirements >= coverage.TotalRequirements;
        }

        private static bool HasPartialSupportedCoverage(SalesforceConfigCoverage coverage)
        {
            return coverage.SupportedRequirements > 0
                   && !IsCompleteSupportedCoverage(coverage);
        }

        private Form CreateConfigCoverageApprovalDialog(SalesforceConfigCoverage coverage)
        {
            var dialog = new Form
            {
                Text = "Review Salesforce Config Coverage",
                Size = new Size(900, 650),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Text = BuildCoverageApprovalHeaderText(coverage),
                ForeColor = Color.Black,
                Padding = new Padding(12, 12, 0, 0)
            };

            var coverageBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                Text = SalesforceConfigPlanFormatter.BuildCoveragePreview(coverage)
            };

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var approveBtn = new Button
            {
                Text = "Supported Only",
                Width = 130,
                Height = 36,
                Top = 12,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Enabled = coverage.SupportedRequirements > 0 && IsCompleteSupportedCoverage(coverage)
            };

            var alternativeBtn = new Button
            {
                Text = "Supported + Alternatives",
                Width = 175,
                Height = 36,
                Top = 12,
                BackColor = Color.FromArgb(218, 126, 0),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Enabled = IsCompleteCoverageWithAlternatives(coverage)
            };

            var partialBtn = new Button
            {
                Text = "Apply Supported Partial",
                Width = 180,
                Height = 36,
                Top = 12,
                BackColor = Color.FromArgb(90, 90, 96),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Enabled = HasPartialSupportedCoverage(coverage)
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Width = 110,
                Height = 36,
                Top = 12,
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            approveBtn.Click += (_, _) =>
            {
                _includeCoverageAlternatives = false;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };

            partialBtn.Click += (_, _) =>
            {
                _includeCoverageAlternatives = false;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };

            alternativeBtn.Click += (_, _) =>
            {
                _includeCoverageAlternatives = true;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };

            cancelBtn.Click += (_, _) =>
            {
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            };

            var buttons = coverage.AlternativeRequirements > 0
                ? new List<Button> { approveBtn, partialBtn, alternativeBtn, cancelBtn }
                : new List<Button> { approveBtn, partialBtn, cancelBtn };
            buttonPanel.Controls.AddRange(buttons.ToArray());
            dialog.Controls.Add(coverageBox);
            dialog.Controls.Add(buttonPanel);
            dialog.Controls.Add(header);
            buttonPanel.Resize += (_, _) => PositionApprovalButtons(buttonPanel, buttons);
            dialog.Shown += (_, _) => PositionApprovalButtons(buttonPanel, buttons);
            return dialog;
        }

        private static string BuildCoverageApprovalHeaderText(SalesforceConfigCoverage coverage)
        {
            var text = $"Coverage: {coverage.SupportedRequirements} of {coverage.TotalRequirements} supported ({coverage.CoveragePercentage}%). Alternatives: {coverage.AlternativeRequirements}.";
            if (IsCompleteSupportedCoverage(coverage))
            {
                return text;
            }

            if (IsCompleteCoverageWithAlternatives(coverage))
            {
                return text + " Use alternatives to apply the complete story.";
            }

            return text + " You can apply only the supported items, but unsupported requirements will remain unfinished.";
        }

        private static void PositionApprovalButtons(Panel buttonPanel, IReadOnlyList<Button> buttons)
        {
            const int gap = 10;
            var totalWidth = buttons.Sum(button => button.Width) + (buttons.Count - 1) * gap;
            var left = Math.Max(20, buttonPanel.ClientSize.Width - totalWidth - 20);

            foreach (var button in buttons)
            {
                button.Left = left;
                left += button.Width + gap;
            }
        }
        private bool ApproveConfigPlan(SalesforceConfigPlan plan)
        {
            using var dialog = CreateConfigPlanApprovalDialog(plan);
            return dialog.ShowDialog() == DialogResult.OK;
        }

        private Form CreateConfigPlanApprovalDialog(SalesforceConfigPlan plan)
        {
            var dialog = new Form
            {
                Text = "Approve Salesforce Config Roadmap",
                Size = new Size(900, 650),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Text = "Review the extracted Salesforce config roadmap before any file diffs are generated",
                ForeColor = Color.Black,
                Padding = new Padding(12, 12, 0, 0)
            };

            var planBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                Text = SalesforceConfigPlanFormatter.BuildPreview(plan)
            };

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var approveBtn = new Button
            {
                Text = "Approve Roadmap",
                Width = 140,
                Height = 36,
                Left = 610,
                Top = 12,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Width = 110,
                Height = 36,
                Left = 760,
                Top = 12,
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            approveBtn.Click += (_, _) =>
            {
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };

            cancelBtn.Click += (_, _) =>
            {
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            };

            buttonPanel.Controls.Add(approveBtn);
            buttonPanel.Controls.Add(cancelBtn);
            dialog.Controls.Add(planBox);
            dialog.Controls.Add(buttonPanel);
            dialog.Controls.Add(header);
            return dialog;
        }
        private bool ApproveChangeSet(FileChangeSet changeSet)
        {
            using var dialog = CreateChangeApprovalDialog(changeSet);
            return dialog.ShowDialog() == DialogResult.OK;
        }

        private Form CreateChangeApprovalDialog(FileChangeSet changeSet)
        {
            var dialog = new Form
            {
                Text = "Approve File Changes",
                Size = new Size(900, 700),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Text = changeSet.Title,
                ForeColor = Color.Black,
                Padding = new Padding(12, 12, 0, 0)
            };

            var diffBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                Text = _codeEditService.BuildDiffPreview(changeSet)
            };

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var approveBtn = new Button
            {
                Text = "Approve",
                Width = 110,
                Height = 36,
                Left = 650,
                Top = 12,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Width = 110,
                Height = 36,
                Left = 770,
                Top = 12,
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            approveBtn.Click += (_, _) =>
            {
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };

            cancelBtn.Click += (_, _) =>
            {
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            };

            buttonPanel.Controls.Add(approveBtn);
            buttonPanel.Controls.Add(cancelBtn);
            dialog.Controls.Add(diffBox);
            dialog.Controls.Add(buttonPanel);
            dialog.Controls.Add(header);
            return dialog;
        }

        private static string BuildReadFilesContext(Dictionary<string, string> readFiles)
        {
            var builder = new StringBuilder();
            var thinkingSpinnerSizePx = GetThinkingSpinnerSizePx();
            builder.AppendLine("Files I have read so far:");
            foreach (var file in readFiles)
            {
                builder.AppendLine($"\n--- {file.Key} ---");
                builder.AppendLine(file.Value.Length > 3000 ? file.Value[..3000] + "..." : file.Value);
            }
            return builder.ToString();
        }

        private string BuildSystemPrompt(string userCommand, SalesforceFiles repoFiles, Dictionary<string, string> preloadedFiles)
        {
            var repoContext = new StringBuilder();
            repoContext.AppendLine($"Repository: {_selectedRepoPath}");
            repoContext.AppendLine($"Total Apex Classes: {repoFiles.Classes.Count}");
            repoContext.AppendLine($"Total Triggers: {repoFiles.Triggers.Count}");
            repoContext.AppendLine($"Total LWC Components: {repoFiles.LwcComponents.Count}");
            repoContext.AppendLine($"Total Aura Components: {repoFiles.AuraComponents.Count}");
            repoContext.AppendLine($"Total Pages: {repoFiles.Pages.Count}");
            repoContext.AppendLine();
            repoContext.AppendLine("AVAILABLE APEX CLASSES:");
            foreach (var cls in repoFiles.Classes.Take(20))
            {
                repoContext.AppendLine($"  - {cls.Name}");
            }

            repoContext.AppendLine();
            repoContext.AppendLine("AVAILABLE TRIGGERS:");
            foreach (var trigger in repoFiles.Triggers.Take(10))
            {
                repoContext.AppendLine($"  - {trigger.Name}");
            }

            repoContext.AppendLine();
            repoContext.AppendLine("AVAILABLE PROFILES:");
            foreach (var profile in repoFiles.ProfileFiles.Take(20))
            {
                repoContext.AppendLine($"  - {profile.Name}");
            }

            var requestHints = BuildRequestHints(userCommand);
            var preloadedContext = preloadedFiles.Count > 0
                ? "\n\nPRELOADED REPO CONTEXT:\n" + BuildReadFilesContext(preloadedFiles)
                : string.Empty;

            return $@"You are an expert Salesforce developer helping modify a local Salesforce DX repository.

REPOSITORY SUMMARY:
{repoContext}
{requestHints}{preloadedContext}

RULES:
1. You do NOT have direct file system access.
2. If you need to inspect a file, request it using exactly: <read_file>relative/path</read_file>
3. For profile FLS changes, do not write profile files. Use this exact self-closing tool tag:
<profile_fls object=""Placement__c"" field=""Testing_Hello__c"" editable_profiles=""Admin.profile-meta.xml;Back Office.profile-meta.xml"" />
4. For non-profile file changes:
   - If the file is NEW, use: <write_file path=""relative/path"">...</write_file>
   - If the file is EXISTING CODE (.cls, .trigger, .js, .html, .css, .page), you MUST use surgical edits:
     <surgical_edit path=""relative/path"">
       <search>exact existing code block</search>
       <replace>new code block</replace>
     </surgical_edit>
   - For existing non-code files (XML/Metadata), you can still use <write_file>.
5. If multiple files must change, return multiple blocks.
6. Do not say you already scanned or wrote files.
7. Do not invent profiles, layouts, classes, permission sets, or generic profile names that are not in the repo.
8. Only use read_only_profiles=""*"" when the user explicitly says other/remaining profiles should be read-only.
9. Follow the exact patterns from files that were provided to you.
10. Only create Apex class/trigger code directly when the user explicitly asks for a new class or trigger.

User Request: {userCommand}";
        }

        private static FileChangeSet CombineChangeSets(string title, IEnumerable<FileChangeSet> changeSets)
        {
            var sets = changeSets.ToList();
            var proposals = sets.SelectMany(cs => cs.Files).ToList();
            var messages = sets
                .Where(cs => cs.Messages != null)
                .SelectMany(cs => cs.Messages!)
                .Distinct()
                .ToList();

            return new FileChangeSet(title, proposals, messages);
        }

        private static bool IsProfileMetadataPath(string relativePath)
        {
            return relativePath.EndsWith(".profile-meta.xml", StringComparison.OrdinalIgnoreCase)
                   || relativePath.EndsWith(".permissionset-meta.xml", StringComparison.OrdinalIgnoreCase);
        }

        private static ProfileFlsRequest? ExtractProfileFlsRequest(string aiResponse)
        {
            var match = Regex.Match(aiResponse, @"<profile_fls\b(?<attrs>[^>]*)/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
            {
                return null;
            }

            var attrs = ParseAttributes(match.Groups["attrs"].Value);
            if (!attrs.TryGetValue("object", out var objectApiName) || !attrs.TryGetValue("field", out var fieldApiName))
            {
                return null;
            }

            attrs.TryGetValue("editable_profiles", out var editableProfiles);
            attrs.TryGetValue("read_only_profiles", out var readOnlyProfiles);
            var readOnlyList = SplitProfileList(readOnlyProfiles);

            return new ProfileFlsRequest(
                objectApiName,
                fieldApiName,
                SplitProfileList(editableProfiles),
                readOnlyList.Where(profile => profile != "*").ToList(),
                readOnlyList.Contains("*"));
        }

        private static ProfileFlsRequest? TryInferProfileFlsRequest(string userCommand, IReadOnlyCollection<RequestedFileWrite> writeRequests)
        {
            if (!RequiresProfileUpdates(userCommand))
            {
                return null;
            }

            var objectApiName = ExtractObjectApiName(userCommand);
            var fieldApiName = ExtractFieldApiNameFromWriteRequests(writeRequests) ?? ExtractFieldApiName(userCommand);
            if (string.IsNullOrWhiteSpace(objectApiName) || string.IsNullOrWhiteSpace(fieldApiName))
            {
                return null;
            }

            var editableProfiles = new List<string>();
            var lowered = userCommand.ToLowerInvariant();
            if (lowered.Contains("admin"))
            {
                editableProfiles.Add("Admin.profile-meta.xml");
            }

            if (lowered.Contains("backoffice") || lowered.Contains("back office"))
            {
                editableProfiles.Add("Back Office.profile-meta.xml");
            }

            var applyReadOnlyToRemainingProfiles = UserExplicitlyRequestsRemainingReadOnly(userCommand);

            return new ProfileFlsRequest(
                objectApiName,
                fieldApiName,
                editableProfiles,
                Array.Empty<string>(),
                applyReadOnlyToRemainingProfiles);
        }

        private static ProfileFlsRequest RefineProfileFlsRequestFromUserCommand(string userCommand, ProfileFlsRequest request)
        {
            var explicitEditableProfiles = ExtractExplicitEditableProfiles(userCommand);
            var explicitReadOnlyProfiles = ExtractExplicitReadOnlyProfiles(userCommand);
            var hasExplicitProfileAccessList = explicitEditableProfiles.Count > 0 || explicitReadOnlyProfiles.Count > 0;
            var applyReadOnlyToRemainingProfiles = UserExplicitlyRequestsRemainingReadOnly(userCommand);

            return new ProfileFlsRequest(
                request.ObjectApiName,
                request.FieldApiName,
                hasExplicitProfileAccessList ? explicitEditableProfiles : request.EditableProfiles,
                explicitReadOnlyProfiles.Count > 0
                    ? explicitReadOnlyProfiles
                    : applyReadOnlyToRemainingProfiles ? request.ReadOnlyProfiles : Array.Empty<string>(),
                applyReadOnlyToRemainingProfiles);
        }

        private static List<string> ExtractExplicitEditableProfiles(string userCommand)
        {
            var profiles = ExtractProfilesFromAccessSection(
                userCommand,
                @"read\s*/?\s*write|read-write|editable|edit access|write access",
                @"read\s+only|read-only|readonly|readable|ready\s*only|readyonly");

            if (profiles.Count > 0)
            {
                return profiles;
            }

            return HasReadWriteAccessPhrase(userCommand) && !HasReadOnlyAccessPhrase(userCommand)
                ? ExtractExplicitProfiles(userCommand)
                : new List<string>();
        }

        private static List<string> ExtractExplicitReadOnlyProfiles(string userCommand)
        {
            var profiles = ExtractProfilesFromAccessSection(
                userCommand,
                @"read\s+only|read-only|readonly|readable|ready\s*only|readyonly",
                @"read\s*/?\s*write|read-write|editable|edit access|write access");

            if (profiles.Count > 0)
            {
                return profiles;
            }

            return HasReadOnlyAccessPhrase(userCommand)
                   && !HasReadWriteAccessPhrase(userCommand)
                   && !UserExplicitlyRequestsRemainingReadOnly(userCommand)
                ? ExtractExplicitProfiles(userCommand)
                : new List<string>();
        }

        private static List<string> ExtractProfilesFromAccessSection(string userCommand, string accessPattern, string nextAccessPattern)
        {
            var matches = Regex.Matches(
                userCommand,
                $@"(?:{accessPattern})\s*(?:for|profiles?)?\s*:?\s*(?<profiles>.*?)(?=(?:{nextAccessPattern})\s*(?:for|profiles?)?\s*:|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var profiles = new List<string>();
            foreach (Match match in matches)
            {
                profiles.AddRange(ExtractExplicitProfilesFromText(match.Groups["profiles"].Value));
            }

            return profiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool HasReadWriteAccessPhrase(string userCommand)
        {
            return Regex.IsMatch(userCommand, @"\b(read\s*/?\s*write|read-write|editable|edit access|write access)\b", RegexOptions.IgnoreCase);
        }

        private static bool HasReadOnlyAccessPhrase(string userCommand)
        {
            return Regex.IsMatch(userCommand, @"\b(read\s+only|read-only|readonly|readable|ready\s*only|readyonly)\b", RegexOptions.IgnoreCase);
        }

        private static List<string> ExtractExplicitProfiles(string userCommand)
        {
            return ExtractExplicitProfilesFromText(ExtractProfileListSegment(userCommand));
        }

        private static List<string> ExtractExplicitProfilesFromText(string text)
        {
            var remaining = text.ToLowerInvariant();
            var matches = new List<string>();
            var knownProfiles = new[]
            {
                ("Tenth Revolution Users.profile-meta.xml", new[] { "Tenth Revolution Users", "Tenth Revolution" }),
                ("Rebura Back Office.profile-meta.xml", new[] { "Rebura Back Office" }),
                ("Back Office.profile-meta.xml", new[] { "Back Office", "BackOffice" }),
                ("LargeStaff.profile-meta.xml", new[] { "LargeStaff", "Large Staff" }),
                ("Recruiter.profile-meta.xml", new[] { "Recruiter" }),
                ("Revolent.profile-meta.xml", new[] { "Revolent" }),
                ("Admin.profile-meta.xml", new[] { "Admin", "Administrator" })
            };

            foreach (var (fileName, aliases) in knownProfiles)
            {
                foreach (var alias in aliases.OrderByDescending(value => value.Length))
                {
                    var pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(alias.ToLowerInvariant()).Replace("\\ ", @"\s+")}(?![A-Za-z0-9])";
                    var match = Regex.Match(remaining, pattern, RegexOptions.IgnoreCase);
                    if (!match.Success)
                    {
                        continue;
                    }

                    matches.Add(fileName);
                    remaining = remaining.Remove(match.Index, match.Length).Insert(match.Index, new string(' ', match.Length));
                    break;
                }
            }

            return matches;
        }

        private static string ExtractProfileListSegment(string userCommand)
        {
            var match = Regex.Match(userCommand, @"following\s+profiles?(?:\s+files?)?(?:\s+as\s+(?:read\s*/?\s*write|read\s+only|read-only|readonly|ready\s*only|readyonly))?\s*:?(?<profiles>.*)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["profiles"].Value : userCommand;
        }
        private static bool UserExplicitlyRequestsRemainingReadOnly(string userCommand)
        {
            var lowered = userCommand.ToLowerInvariant();
            var mentionsRemainingProfiles = lowered.Contains("other profile")
                || lowered.Contains("other profiles")
                || lowered.Contains("remaining profile")
                || lowered.Contains("remaining profiles")
                || lowered.Contains("all other profiles");
            var asksForReadOnly = lowered.Contains("read access only")
                || lowered.Contains("read-only")
                || lowered.Contains("readonly")
                || lowered.Contains("read only")
                || lowered.Contains("ready only")
                || lowered.Contains("readyonly");

            return mentionsRemainingProfiles && asksForReadOnly;
        }
        private static Dictionary<string, string> ParseAttributes(string text)
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(text, @"(?<name>[\w_]+)\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase))
            {
                attrs[match.Groups["name"].Value] = match.Groups["value"].Value;
            }

            return attrs;
        }

        private static List<string> SplitProfileList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            return value
                .Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(profile => profile.Trim())
                .Where(profile => !string.IsNullOrWhiteSpace(profile))
                .ToList();
        }

        private static string? ExtractObjectApiName(string userCommand)
        {
            var match = Regex.Match(userCommand, @"\b(?<object>[A-Za-z][A-Za-z0-9_]*__c)\b\s+object", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return NormalizeKnownObjectApiName(match.Groups["object"].Value);
            }

            match = Regex.Match(userCommand, @"\bon\s+(?<object>[A-Za-z][A-Za-z0-9_]*__c)\b", RegexOptions.IgnoreCase);
            return match.Success ? NormalizeKnownObjectApiName(match.Groups["object"].Value) : null;
        }

        private static string? ExtractFieldApiNameFromWriteRequests(IEnumerable<RequestedFileWrite> writeRequests)
        {
            foreach (var write in writeRequests)
            {
                var fullNameMatch = Regex.Match(write.Content, @"<fullName>(?<field>[^<]+)</fullName>", RegexOptions.IgnoreCase);
                if (fullNameMatch.Success)
                {
                    return fullNameMatch.Groups["field"].Value.Trim();
                }

                var pathMatch = Regex.Match(write.RelativePath, @"fields[\\/](?<field>[^\\/]+)\.field-meta\.xml$", RegexOptions.IgnoreCase);
                if (pathMatch.Success)
                {
                    return pathMatch.Groups["field"].Value.Trim();
                }
            }

            return null;
        }

        private static string NormalizeKnownObjectApiName(string objectApiName)
        {
            return objectApiName.Equals("placemet__c", StringComparison.OrdinalIgnoreCase)
                ? "Placement__c"
                : objectApiName;
        }

        private static string? ExtractFieldApiName(string userCommand)
        {
            var match = Regex.Match(userCommand, @"name\s+should\s+(?:be\s+)?[""']?(?<field>[A-Za-z][A-Za-z0-9_]*)(?:__c)?[""']?", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var field = match.Groups["field"].Value.Trim();
                return field.EndsWith("__c", StringComparison.OrdinalIgnoreCase) ? field : field + "__c";
            }

            match = Regex.Match(userCommand, @"field\s+(?:called|named|with name)\s+[""']?(?<field>[A-Za-z][A-Za-z0-9_]*)(?:__c)?[""']?", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var field = match.Groups["field"].Value.Trim();
                return field.EndsWith("__c", StringComparison.OrdinalIgnoreCase) ? field : field + "__c";
            }

            match = Regex.Match(userCommand, @"\b(?<field>[A-Za-z][A-Za-z0-9_]*)(?:__c)?\s+field\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var field = match.Groups["field"].Value.Trim();
                return field.EndsWith("__c", StringComparison.OrdinalIgnoreCase) ? field : field + "__c";
            }

            return null;
        }
        private static bool RequiresProfileUpdates(string userCommand)
        {
            var lowered = userCommand.ToLowerInvariant();
            return lowered.Contains("profile") || lowered.Contains("fls");
        }

        private static bool HasProfileWrites(IReadOnlyCollection<RequestedFileWrite> writeRequests)
        {
            return writeRequests.Any(write =>
                write.RelativePath.EndsWith(".profile-meta.xml", StringComparison.OrdinalIgnoreCase) ||
                write.RelativePath.EndsWith(".permissionset-meta.xml", StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildRequestHints(string userCommand)
        {
            var lowered = userCommand.ToLowerInvariant();
            var hints = new StringBuilder();

            if (lowered.Contains("placement__c") && (lowered.Contains("field") || lowered.Contains("fls") || lowered.Contains("profile")))
            {
                hints.AppendLine();
                hints.AppendLine("REQUEST-SPECIFIC HINTS:");
                hints.AppendLine("- Custom field metadata for Placement__c belongs under force-app/main/default/objects/Placement__c/fields.");
                hints.AppendLine("- Profile FLS updates must use the <profile_fls ... /> tool tag. Do not return profile file XML.");
                hints.AppendLine("- Admin.profile-meta.xml and Back Office.profile-meta.xml are real existing profile files in this repo.");
                hints.AppendLine("- When the user says 'other profiles', use the remaining existing AVAILABLE PROFILES listed above.");
                hints.AppendLine("- For field + FLS requests, do not stop after creating the field file; include all required profile file updates in the same response.");
            }

            return hints.ToString();
        }

        private async Task<Dictionary<string, string>> BuildAutomaticReadFilesContextAsync(string userCommand)
        {
            var readFiles = new Dictionary<string, string>();
            var lowered = userCommand.ToLowerInvariant();

            if (lowered.Contains("placement__c") && lowered.Contains("field"))
            {
                var placementFieldDirectory = await _repoContextService.ReadFileFromRepoAsync(_selectedRepoPath!, "force-app/main/default/objects/Placement__c/fields");
                if (!string.IsNullOrWhiteSpace(placementFieldDirectory))
                {
                    readFiles["force-app/main/default/objects/Placement__c/fields"] = placementFieldDirectory;
                }
            }

            if (lowered.Contains("profile") || lowered.Contains("fls"))
            {
                var profilesDirectory = await _repoContextService.ReadFileFromRepoAsync(_selectedRepoPath!, "force-app/main/default/profiles");
                if (!string.IsNullOrWhiteSpace(profilesDirectory))
                {
                    readFiles["force-app/main/default/profiles"] = profilesDirectory;
                }
            }

            return readFiles;
        }

        private static string CleanupGeneratedResponse(string aiResponse)
        {
            return aiResponse.Replace("```apex", string.Empty).Replace("```", string.Empty).Trim();
        }

        private static bool ShouldFallbackToGeneratedCode(string userCommand, string aiResponse)
        {
            var explicitCodeRequest = userCommand.Contains("class", StringComparison.OrdinalIgnoreCase)
                || userCommand.Contains("trigger", StringComparison.OrdinalIgnoreCase)
                || userCommand.Contains("apex", StringComparison.OrdinalIgnoreCase);

            if (!explicitCodeRequest)
            {
                return false;
            }

            var trimmed = aiResponse.TrimStart();
            return Regex.IsMatch(trimmed, @"^(public\s+|private\s+|global\s+|trigger\s+)", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(trimmed, @"\bclass\s+\w+", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeToolNarration(string aiResponse)
        {
            return aiResponse.Contains("Let me", StringComparison.OrdinalIgnoreCase)
                   || aiResponse.Contains("Now let me", StringComparison.OrdinalIgnoreCase)
                   || aiResponse.Contains("<write_to_file", StringComparison.OrdinalIgnoreCase)
                   || aiResponse.Contains("<write_file", StringComparison.OrdinalIgnoreCase)
                   || aiResponse.Contains("filepath:", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> AnswerInformationQuestionAsync(string userCommand)
        {
            var systemPrompt = $@"You are a Salesforce AI assistant. Answer informatively. DO NOT generate or save code.
Repository: {_selectedRepoPath}
Current Org: {_selectedOrgAlias ?? "Not selected"}
Files: {GetRepoStatsSummary()}";

            return await _deepSeekClient.SendChatAsync(DeepSeekModels.Normal, systemPrompt, userCommand, 0.7, 500);
        }

        private static bool IsInformationalQuestion(string userCommand)
        {
            var lowered = userCommand.ToLowerInvariant();
            var actionKeywords = new[] { "add", "create", "modify", "update", "delete", "write", "save" };
            if (actionKeywords.Any(lowered.Contains))
            {
                return false;
            }

            var informationalKeywords = new[] { "can you access", "can u access", "what can you do", "help", "how to", "explain" };
            return informationalKeywords.Any(lowered.Contains);
        }

        private async Task<string> DeployToOrgAsync()
        {
            if (string.IsNullOrWhiteSpace(_selectedOrgAlias))
            {
                return "No org selected. Please select a target org first using 'Select Org' button.";
            }

            var latestVersion = await _salesforceCliService.GetLatestApiVersionAsync(_selectedOrgAlias);
            if (!string.IsNullOrEmpty(latestVersion))
            {
                AppendToChat($"Latest Salesforce API Version for {_selectedOrgAlias}: {latestVersion}", Color.LightBlue);
            }

            var commandText = $"sf project deploy start --source-dir force-app --target-org {_selectedOrgAlias} --wait 10";
            var approval = _commandApprovalService.Evaluate(commandText, _selectedOrgAlias);
            if (approval.IsBlocked)
            {
                return $"Command blocked: {approval.Reason}";
            }

            if (approval.RequiresApproval && !ApproveCommand(approval))
            {
                return "Deployment was cancelled because command approval was declined.";
            }

            AppendToChat($"Deploying to {_selectedOrgAlias}...", Color.Yellow);
            var result = await _salesforceCliService.DeployToOrgAsync(_selectedRepoPath!, _selectedOrgAlias);
            
            if (!string.IsNullOrEmpty(result.Command))
            {
                AppendToChat($"[DEBUG] Executing command: {result.Command}", Color.Gray);
            }

            var output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
            return result.ExitCode == 0
                ? $"Deployment successful to {_selectedOrgAlias}!\n\n{output}"
                : $"Deployment failed to {_selectedOrgAlias}:\n{result.StandardError}\n{result.StandardOutput}";
        }

        private async Task<string> ApplyAndDeployChangeSetAsync(FileChangeSet changeSet)
        {
            ReportProcessingStep($"Applying {changeSet.Files.Count} approved file change(s) locally...");
            await _codeEditService.ApplyChangeSetAsync(_selectedRepoPath!, changeSet);

            if (string.IsNullOrWhiteSpace(_selectedOrgAlias))
            {
                return " [Applied locally, but NOT deployed: No org selected]";
            }

            var relativePaths = changeSet.Files.Select(f => f.RelativePath).ToList();
            ReportProcessingStep($"Running Salesforce dry-run validation for {relativePaths.Count} changed file(s)...");
            AppendToChat($"Validating {relativePaths.Count} changed file(s) against {_selectedOrgAlias} (dry-run)...", Color.Yellow);
            var validation = await _salesforceValidationService.ValidateDeploymentAsync(_selectedRepoPath!, _selectedOrgAlias, relativePaths);
            if (!validation.IsSuccess)
            {
                ReportProcessingStep("Dry-run validation failed. Rolling back local file changes...");
                AppendToChat($"Validation failed for {_selectedOrgAlias}. Rolling back local changes...", Color.Red);
                await RollbackChangeSetAsync(_selectedRepoPath!, changeSet);
                return $" [DEPLOYMENT BLOCKED by validation failure: {validation.Output}. Local changes were rolled back.]";
            }

            ReportProcessingStep("Retrieving target org API version...");
            var latestVersion = await _salesforceCliService.GetLatestApiVersionAsync(_selectedOrgAlias);
            if (!string.IsNullOrEmpty(latestVersion))
            {
                AppendToChat($"Latest Salesforce API Version for {_selectedOrgAlias}: {latestVersion}", Color.LightBlue);
            }

            ReportProcessingStep($"Deploying {changeSet.Files.Count} validated file(s) to {_selectedOrgAlias}...");
            AppendToChat($"Deploying {changeSet.Files.Count} changed file(s) to {_selectedOrgAlias}...", Color.Yellow);
            var result = await _salesforceCliService.DeployFilesToOrgAsync(_selectedRepoPath!, _selectedOrgAlias, relativePaths, 10, latestVersion);

            if (!string.IsNullOrEmpty(result.Command))
            {
                AppendToChat($"[DEBUG] Executing command: {result.Command}", Color.Gray);
            }

            if (result.ExitCode == 0)
            {
                AppendToChat($"Deployment successful to {_selectedOrgAlias}.", Color.LightGreen);
                return $" [Applied and Deployed to {_selectedOrgAlias}]";
            }
            else
            {
                AppendToChat($"Deployment failed to {_selectedOrgAlias}.", Color.Red);
                var fullError = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : $"{result.StandardError}\n{result.StandardOutput}";
                return $" [Applied locally, DEPLOYMENT FAILED: {fullError}]";
            }
        }

        private static async Task RollbackChangeSetAsync(string repoPath, FileChangeSet changeSet)
        {
            foreach (var file in changeSet.Files)
            {
                var fullPath = Path.Combine(repoPath, file.RelativePath);
                if (file.FileExists)
                {
                    await System.IO.File.WriteAllTextAsync(fullPath, file.ExistingContent);
                }
                else if (System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                }
            }
        }

        private bool ApproveCommand(CommandApprovalRequest request)
        {
            using var dialog = new Form
            {
                Text = "Approve Command",
                Size = new Size(760, 320),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };

            var body = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 10),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                Text = $"Description: {request.Description}\nRisk: {request.RiskLevel}\nReason: {request.Reason}\n\nCommand:\n{request.CommandText}"
            };

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var approveBtn = new Button
            {
                Text = "Run Command",
                Width = 120,
                Height = 36,
                Left = 500,
                Top = 12,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Width = 100,
                Height = 36,
                Left = 630,
                Top = 12,
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };

            approveBtn.Click += (_, _) =>
            {
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };

            cancelBtn.Click += (_, _) =>
            {
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            };

            buttonPanel.Controls.Add(approveBtn);
            buttonPanel.Controls.Add(cancelBtn);
            dialog.Controls.Add(body);
            dialog.Controls.Add(buttonPanel);
            return dialog.ShowDialog() == DialogResult.OK;
        }

        public static void AppendToChatStatic(string text, Color color)
        {
            if (Application.OpenForms[0] is Form1 form)
            {
                form.Invoke(new Action(() => form.AppendToChat(text, color)));
            }
        }

        private void AppendToChat(string text, Color color)
        {
            if (rtbChat.InvokeRequired)
            {
                rtbChat.Invoke(() => AppendToChat(text, color));
                return;
            }

            rtbChat.SelectionStart = rtbChat.TextLength;
            rtbChat.SelectionLength = 0;
            rtbChat.SelectionColor = color;
            rtbChat.AppendText(text + Environment.NewLine);
            rtbChat.ScrollToCaret();
        }

        private static List<RequestedFileWrite> ExtractFileWrites(string aiResponse)
        {
            var writes = new List<RequestedFileWrite>();
            var matches = Regex.Matches(
                aiResponse,
                @"<write_(?:to_)?file\s+path=[""']([^""']+)[""']>(.*?)</write_(?:to_)?file>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                var path = match.Groups[1].Value.Trim();
                var content = match.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(content))
                {
                    writes.Add(new RequestedFileWrite(path, content));
                }
            }

            return writes;
        }

        private static string? ExtractFileReadRequest(string aiResponse)
        {
            var match = Regex.Match(aiResponse, @"<read_file>\s*(?:path=[""'])?\s*([^""'\n>]+?)\s*(?:[""'])?\s*>?\s*</read_file>", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            match = Regex.Match(aiResponse, @"<read_file>(.*?)(?=</read_file>|$)", RegexOptions.IgnoreCase);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                return match.Groups[1].Value.Trim();
            }

            match = Regex.Match(aiResponse, @"[A-Za-z]:\\(?:force-app\\main\\default\\objects\\[^\\]+)(?:\\[^\\]+)?", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[0].Value.Trim() : null;
        }

        private static List<FileEditPlan> ExtractSurgicalEdits(string aiResponse)
        {
            var plans = new Dictionary<string, FileEditPlan>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(aiResponse, @"<surgical_edit\s+path=[""'](?<path>[^""']+)[""']>(?<edits>.*?)</surgical_edit>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                var path = match.Groups["path"].Value.Trim();
                var editsText = match.Groups["edits"].Value;
                var editMatches = Regex.Matches(editsText, @"<search>(?<search>.*?)</search>\s*<replace>(?<replace>.*?)</replace>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                if (editMatches.Count == 0) continue;

                if (!plans.TryGetValue(path, out var plan))
                {
                    plan = new FileEditPlan(path, new List<CodeEdit>());
                    plans[path] = plan;
                }

                foreach (Match editMatch in editMatches)
                {
                    plan.Edits.Add(new CodeEdit(editMatch.Groups["search"].Value, editMatch.Groups["replace"].Value));
                }
            }

            return plans.Values.ToList();
        }
    }
}



















































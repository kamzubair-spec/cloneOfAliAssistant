using System.ComponentModel;
using System.Diagnostics;
using eZBERP_AI_IDE.Models;
using eZBERP_AI_IDE.Services;

namespace eZBERP_AI_IDE;

public sealed class GitReviewForm : Form
{
    private readonly string _repoPath;
    private readonly GitService _gitService = new();
    private readonly BitbucketPullRequestService _pullRequestService;
    private readonly BindingList<GitChangedFile> _changedFiles = new();

    private readonly DataGridView _filesGrid;
    private readonly RichTextBox _diffViewer;
    private readonly Label _branchLabel;
    private readonly Label _statusLabel;
    private readonly Button _refreshButton;
    private readonly Button _createBranchButton;
    private readonly Button _stageAllButton;
    private readonly Button _commitButton;
    private readonly Button _pushButton;
    private readonly Button _createPrButton;
    private readonly Button _closeButton;

    public GitReviewForm(string repoPath)
    {
        _repoPath = repoPath;
        _pullRequestService = new BitbucketPullRequestService(_gitService);

        Text = "Git Review";
        Size = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(245, 246, 248);
        ForeColor = Color.FromArgb(20, 24, 28);

        _refreshButton = CreateButton("Refresh", 16);
        _createBranchButton = CreateButton("Create Branch", 110);
        _stageAllButton = CreateButton("Stage All", 240);
        _commitButton = CreateButton("Commit", 334);
        _pushButton = CreateButton("Push", 428);
        _createPrButton = CreateButton("Create PR", 522);
        _closeButton = CreateButton("Close", 1050);

        _branchLabel = new Label
        {
            Location = new Point(650, 20),
            Size = new Size(386, 24),
            Text = "Branch: loading...",
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(15, 23, 42),
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };

        _statusLabel = new Label
        {
            Location = new Point(16, 52),
            Size = new Size(1128, 24),
            Text = "Ready",
            ForeColor = Color.FromArgb(55, 65, 81)
        };

        _filesGrid = new DataGridView
        {
            Location = new Point(16, 82),
            Size = new Size(330, 610),
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            MultiSelect = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            DataSource = _changedFiles,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _filesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Changed Files",
            DataPropertyName = nameof(GitChangedFile.Display),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        _diffViewer = new RichTextBox
        {
            Location = new Point(360, 82),
            Size = new Size(784, 610),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 30, 30),
            Font = new Font("Consolas", 9),
            ReadOnly = true,
            WordWrap = false,
            BorderStyle = BorderStyle.FixedSingle
        };

        Controls.AddRange(new Control[]
        {
            _refreshButton,
            _createBranchButton,
            _stageAllButton,
            _commitButton,
            _pushButton,
            _createPrButton,
            _closeButton,
            _branchLabel,
            _statusLabel,
            _filesGrid,
            _diffViewer
        });

        _refreshButton.Click += async (_, _) => await RefreshChangedFilesAsync();
        _createBranchButton.Click += async (_, _) => await CreateBranchAsync();
        _stageAllButton.Click += async (_, _) => await RunGitActionAsync("Staging all changes...", () => _gitService.StageAllAsync(_repoPath));
        _commitButton.Click += async (_, _) => await CommitAsync();
        _pushButton.Click += async (_, _) => await RunGitActionAsync("Pushing current branch...", () => _gitService.PushCurrentBranchAsync(_repoPath));
        _createPrButton.Click += async (_, _) => await CreatePullRequestAsync();
        _closeButton.Click += (_, _) => Close();
        _filesGrid.SelectionChanged += async (_, _) => await LoadSelectedDiffAsync();
        Shown += async (_, _) => await RefreshChangedFilesAsync();
    }

    private Button CreateButton(string text, int x)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, 16),
            Size = new Size(112, 30),
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
        return button;
    }

    private async Task RefreshChangedFilesAsync()
    {
        await RunUiActionAsync("Reading git status...", async () =>
        {
            await RefreshCurrentBranchAsync();

            _changedFiles.Clear();
            foreach (var file in await _gitService.GetChangedFilesAsync(_repoPath))
            {
                _changedFiles.Add(file);
            }

            _statusLabel.Text = _changedFiles.Count == 0
                ? "Working tree is clean."
                : $"Found {_changedFiles.Count} changed file(s).";

            if (_changedFiles.Count == 0)
            {
                _diffViewer.Clear();
            }
        });
    }

    private async Task LoadSelectedDiffAsync()
    {
        if (_filesGrid.SelectedRows.Count == 0)
        {
            return;
        }

        if (_filesGrid.SelectedRows[0].DataBoundItem is not GitChangedFile file)
        {
            return;
        }

        await RunUiActionAsync($"Loading diff for {file.Path}...", async () =>
        {
            if (file.Status.Contains("??", StringComparison.Ordinal))
            {
                RenderNewFileDiff(file.Path);
                _statusLabel.Text = $"Showing new file content for {file.Path}";
                return;
            }

            var diff = await _gitService.GetDiffAsync(_repoPath, file.Path);
            if (diff.IsSuccess && !string.IsNullOrWhiteSpace(diff.Output))
            {
                RenderDiff(diff.Output);
                _statusLabel.Text = $"Showing unstaged diff for {file.Path}";
                return;
            }

            var stagedDiff = await _gitService.GetStagedDiffAsync(_repoPath, file.Path);
            if (stagedDiff.IsSuccess && !string.IsNullOrWhiteSpace(stagedDiff.Output))
            {
                RenderDiff(stagedDiff.Output);
                _statusLabel.Text = $"Showing staged diff for {file.Path}";
                return;
            }

            _diffViewer.Text = "No text diff available for this file.";
            _statusLabel.Text = $"No text diff available for {file.Path}";
        });
    }

    private void RenderNewFileDiff(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_repoPath, relativePath));
        var repoRoot = Path.GetFullPath(_repoPath);
        if (!fullPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            _diffViewer.Text = "New file content could not be loaded safely.";
            return;
        }

        var lines = File.ReadAllLines(fullPath);
        var syntheticDiff = new List<string>
        {
            "diff --git a/" + relativePath + " b/" + relativePath,
            "new file mode 100644",
            "--- /dev/null",
            "+++ b/" + relativePath,
            "@@ new file @@"
        };
        syntheticDiff.AddRange(lines.Select(line => "+" + line));
        RenderDiff(string.Join(Environment.NewLine, syntheticDiff));
    }

    private async Task CreateBranchAsync()
    {
        var branchName = PromptForText("Create Branch", "Branch name:", "codex/");
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return;
        }

        await RunGitActionAsync($"Creating branch {branchName}...", () => _gitService.CreateAndCheckoutBranchAsync(_repoPath, branchName));
    }

    private async Task RefreshCurrentBranchAsync()
    {
        var branchResult = await _gitService.GetCurrentBranchAsync(_repoPath);
        _branchLabel.Text = branchResult.IsSuccess && !string.IsNullOrWhiteSpace(branchResult.Output)
            ? "Branch: " + branchResult.Output.Trim()
            : "Branch: unavailable";
    }

    private async Task CommitAsync()
    {
        var message = PromptForText("Commit Changes", "Commit message:", string.Empty);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await RunGitActionAsync("Committing staged changes...", () => _gitService.CommitAsync(_repoPath, message));
    }

    private async Task CreatePullRequestAsync()
    {
        var branchResult = await _gitService.GetCurrentBranchAsync(_repoPath);
        if (!branchResult.IsSuccess || string.IsNullOrWhiteSpace(branchResult.Output))
        {
            ShowError("Unable to determine the current branch.", branchResult.CombinedOutput);
            return;
        }

        var sourceBranch = branchResult.Output.Trim();
        var targetBranch = PromptForText("Create Pull Request", "Target branch:", GetSetting("EZBERP_PR_TARGET_BRANCH", "develop"));
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            return;
        }

        var title = PromptForText("Create Pull Request", "PR title:", sourceBranch);
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        await RunUiActionAsync("Creating Bitbucket pull request...", async () =>
        {
            var url = await _pullRequestService.CreatePullRequestAsync(
                _repoPath,
                sourceBranch,
                targetBranch,
                title,
                "Created from eZBERP AI IDE Git Review.");

            _statusLabel.Text = "Pull request created.";
            if (MessageBox.Show(url, "Pull Request Created", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        });
    }

    private async Task RunGitActionAsync(string status, Func<Task<GitCommandResult>> action)
    {
        await RunUiActionAsync(status, async () =>
        {
            var result = await action();
            if (!result.IsSuccess)
            {
                ShowError(result.Command, result.CombinedOutput);
                return;
            }

            _statusLabel.Text = string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? $"{status.TrimEnd('.')} completed."
                : result.CombinedOutput.Trim();
            await RefreshCurrentBranchAsync();
            await RefreshChangedFilesAsync();
        });
    }

    private async Task RunUiActionAsync(string status, Func<Task> action)
    {
        SetBusy(true, status);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError("Git Review Error", ex.Message);
        }
        finally
        {
            SetBusy(false, _statusLabel.Text);
        }
    }

    private void SetBusy(bool isBusy, string status)
    {
        _statusLabel.Text = status;
        Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
        foreach (var button in new[] { _refreshButton, _createBranchButton, _stageAllButton, _commitButton, _pushButton, _createPrButton })
        {
            button.Enabled = !isBusy;
        }
    }

    private void RenderDiff(string diff)
    {
        _diffViewer.Clear();
        foreach (var line in diff.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var foreColor = Color.FromArgb(30, 30, 30);
            var backColor = Color.White;
            var style = FontStyle.Regular;

            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                foreColor = Color.FromArgb(24, 94, 43);
                backColor = Color.FromArgb(221, 244, 228);
            }
            else if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
            {
                foreColor = Color.FromArgb(140, 32, 32);
                backColor = Color.FromArgb(255, 225, 225);
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                foreColor = Color.FromArgb(9, 83, 163);
                backColor = Color.FromArgb(229, 241, 255);
                style = FontStyle.Bold;
            }
            else if (line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal))
            {
                foreColor = Color.FromArgb(70, 70, 70);
                backColor = Color.FromArgb(238, 238, 238);
                style = FontStyle.Bold;
            }

            AppendDiffLine(line, foreColor, backColor, style);
        }
    }

    private void AppendDiffLine(string line, Color foreColor, Color backColor, FontStyle style)
    {
        var start = _diffViewer.TextLength;
        _diffViewer.AppendText(line + Environment.NewLine);
        _diffViewer.Select(start, line.Length);
        _diffViewer.SelectionColor = foreColor;
        _diffViewer.SelectionBackColor = backColor;
        _diffViewer.SelectionFont = new Font(_diffViewer.Font, style);
        _diffViewer.Select(_diffViewer.TextLength, 0);
    }

    private static string PromptForText(string title, string label, string defaultValue)
    {
        using var prompt = new Form
        {
            Text = title,
            Size = new Size(520, 150),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.White,
            ForeColor = Color.Black,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var labelControl = new Label
        {
            Text = label,
            Location = new Point(16, 18),
            Size = new Size(470, 20)
        };
        var textBox = new TextBox
        {
            Text = defaultValue,
            Location = new Point(16, 44),
            Size = new Size(470, 24)
        };
        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(304, 78),
            Size = new Size(86, 28)
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(400, 78),
            Size = new Size(86, 28)
        };

        prompt.Controls.AddRange(new Control[] { labelControl, textBox, okButton, cancelButton });
        prompt.AcceptButton = okButton;
        prompt.CancelButton = cancelButton;

        return prompt.ShowDialog() == DialogResult.OK ? textBox.Text.Trim() : string.Empty;
    }

    private static string GetSetting(string name, string fallback)
    {
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
            ?? fallback;
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        // 
        // GitReviewForm
        // 
        ClientSize = new Size(813, 496);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Name = "GitReviewForm";
        ResumeLayout(false);

    }

    private static void ShowError(string title, string message)
    {
        MessageBox.Show(
            string.IsNullOrWhiteSpace(message) ? title : message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}

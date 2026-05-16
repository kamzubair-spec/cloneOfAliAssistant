namespace eZBERP_AI_IDE
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            btnSelectOrg = new Button();
            btnSelectRepo = new Button();
            lblRemainingBalance = new Label();
            rtbChat = new RichTextBox();
            btnSend = new Button();
            lblSelectedRepo = new Label();
            lblSelectedOrg = new Label();
            lblProcessing = new Label();
            picLoading = new PictureBox();
            picAiLogo = new PictureBox();
            lblAiProvider = new Label();
            SuspendLayout();
            // 
            // btnSelectOrg
            // 
            btnSelectOrg.Location = new Point(24, 20);
            btnSelectOrg.Name = "btnSelectOrg";
            btnSelectOrg.Size = new Size(120, 32);
            btnSelectOrg.TabIndex = 0;
            btnSelectOrg.Text = "Select Org";
            btnSelectOrg.UseVisualStyleBackColor = true;
            // 
            // btnSelectRepo
            // 
            btnSelectRepo.Location = new Point(156, 20);
            btnSelectRepo.Name = "btnSelectRepo";
            btnSelectRepo.Size = new Size(120, 32);
            btnSelectRepo.TabIndex = 1;
            btnSelectRepo.Text = "Select Repo";
            btnSelectRepo.UseVisualStyleBackColor = true;
            // 
            // lblAiProvider
            // 
            lblAiProvider.AutoSize = true;
            lblAiProvider.Location = new Point(324, 28);
            lblAiProvider.Name = "lblAiProvider";
            lblAiProvider.Size = new Size(100, 15);
            lblAiProvider.TabIndex = 10;
            lblAiProvider.Text = "Powered by ...";
            lblAiProvider.ForeColor = Color.LightGray;
            // 
            // lblRemainingBalance
            // 
            lblRemainingBalance.AutoSize = true;
            lblRemainingBalance.Location = new Point(593, 8);
            lblRemainingBalance.Name = "lblRemainingBalance";
            lblRemainingBalance.Size = new Size(183, 15);
            lblRemainingBalance.TabIndex = 2;
            lblRemainingBalance.Text = "Remaining Balance: Not available";
            // 
            // rtbChat
            // 
            rtbChat.Location = new Point(24, 68);
            rtbChat.Name = "rtbChat";
            rtbChat.Size = new Size(752, 286);
            rtbChat.TabIndex = 3;
            rtbChat.Text = "";
            // 
            // btnSend
            // 
            btnSend.Location = new Point(656, 384);
            btnSend.Name = "btnSend";
            btnSend.Size = new Size(120, 32);
            btnSend.TabIndex = 6;
            btnSend.Text = "Send";
            btnSend.UseVisualStyleBackColor = true;
            // 
            // lblSelectedRepo
            // 
            lblSelectedRepo.AutoSize = true;
            lblSelectedRepo.Location = new Point(24, 452);
            lblSelectedRepo.Name = "lblSelectedRepo";
            lblSelectedRepo.Size = new Size(153, 15);
            lblSelectedRepo.TabIndex = 5;
            lblSelectedRepo.Text = "Selected Repo: Not selected";
            // 
            // lblSelectedOrg
            // 
            lblSelectedOrg.AutoSize = true;
            lblSelectedOrg.Location = new Point(592, 28);
            lblSelectedOrg.Name = "lblSelectedOrg";
            lblSelectedOrg.Size = new Size(146, 15);
            lblSelectedOrg.TabIndex = 4;
            lblSelectedOrg.Text = "Selected Org: Not selected";
            // 
            // lblProcessing
            // 
            lblProcessing.AutoSize = true;
            lblProcessing.Location = new Point(652, 475);
            lblProcessing.Name = "lblProcessing";
            lblProcessing.Size = new Size(106, 15);
            lblProcessing.TabIndex = 7;
            lblProcessing.Text = "Processing request";
            lblProcessing.Visible = false;
            // 
            // picLoading
            // 
            picLoading.Location = new Point(655, 452);
            picLoading.Name = "picLoading";
            picLoading.Size = new Size(120, 20);
            picLoading.SizeMode = PictureBoxSizeMode.Zoom;
            picLoading.TabIndex = 8;
            picLoading.TabStop = false;
            picLoading.Visible = false;
            // 
            // picAiLogo
            // 
            picAiLogo.Location = new Point(286, 20);
            picAiLogo.Name = "picAiLogo";
            picAiLogo.Size = new Size(32, 32);
            picAiLogo.SizeMode = PictureBoxSizeMode.Zoom;
            picAiLogo.TabIndex = 9;
            picAiLogo.TabStop = false;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 492);
            Controls.Add(lblAiProvider);
            Controls.Add(picAiLogo);
            Controls.Add(picLoading);
            Controls.Add(lblProcessing);
            Controls.Add(btnSend);
            Controls.Add(lblSelectedRepo);
            Controls.Add(lblSelectedOrg);
            Controls.Add(rtbChat);
            Controls.Add(lblRemainingBalance);
            Controls.Add(btnSelectRepo);
            Controls.Add(btnSelectOrg);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MaximizeBox = false;
            Name = "Form1";
            Text = "eZBERP AI Coder";
            ((System.ComponentModel.ISupportInitialize)picAiLogo).EndInit();
            ((System.ComponentModel.ISupportInitialize)picLoading).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button btnSelectOrg;
        private Button btnSelectRepo;
        private Label lblRemainingBalance;
        private RichTextBox rtbChat;
        private Button btnSend;
        private Label lblSelectedRepo;
        private Label lblSelectedOrg;
        private Label lblProcessing;
        private PictureBox picLoading;
        private PictureBox picAiLogo;
        private Label lblAiProvider;
    }
}

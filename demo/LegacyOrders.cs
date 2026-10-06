// A plain WinForms app in the style of designer-generated code. It knows nothing about XUL-J:
// run it directly (Application.Run) or serve it with xulj-host.exe.
using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LegacyOrders
{
    public class MainForm : Form
    {
        MenuStrip menu;
        ToolStripMenuItem fileMenu, newOrderItem, exitItem, helpMenu, aboutItem;
        ToolStrip tools;
        ToolStripButton deleteLastButton, clearButton;
        ToolStripComboBox currencyBox;
        TabControl tabs;
        TabPage ordersPage, importPage, settingsPage;
        GroupBox newOrderGroup;
        Label customerLabel, productLabel, quantityLabel, errorLabel;
        TextBox customerText;
        ComboBox productCombo;
        NumericUpDown quantityUpDown;
        CheckBox expressCheck;
        Button addButton;
        DataGridView ordersGrid;
        Label importLabel;
        Button importButton;
        ProgressBar importProgress;
        ListBox importLog;
        GroupBox roundingGroup;
        RadioButton roundNone, roundUp;
        CheckBox confirmCheck;
        Label apiKeyLabel;
        TextBox apiKeyText;
        StatusStrip status;
        ToolStripStatusLabel statusLabel, clockLabel;
        Timer clockTimer, importTimer;

        static readonly string[] Products = { "Widget", "Gadget", "Gizmo", "Doohickey" };
        static readonly decimal[] Prices = { 4.50m, 12.00m, 7.25m, 19.99m };
        int nextOrder = 1001, importLeft;

        public MainForm()
        {
            InitializeComponent();
            productCombo.Items.AddRange(Products);
            productCombo.SelectedIndex = 0;
            currencyBox.Items.AddRange(new object[] { "EUR", "USD", "GBP" });
            currencyBox.SelectedIndex = 0;
        }

        void InitializeComponent()
        {
            menu = new MenuStrip();
            fileMenu = new ToolStripMenuItem("&File");
            newOrderItem = new ToolStripMenuItem("&New order") { Name = "newOrderItem", ShortcutKeys = Keys.Control | Keys.N };
            exitItem = new ToolStripMenuItem("E&xit") { Name = "exitItem" };
            helpMenu = new ToolStripMenuItem("&Help");
            aboutItem = new ToolStripMenuItem("&About") { Name = "aboutItem" };
            tools = new ToolStrip();
            deleteLastButton = new ToolStripButton("Delete last") { Name = "deleteLastButton" };
            clearButton = new ToolStripButton("Clear all") { Name = "clearButton" };
            currencyBox = new ToolStripComboBox { Name = "currencyBox", DropDownStyle = ComboBoxStyle.DropDownList };
            tabs = new TabControl();
            ordersPage = new TabPage("Orders") { Name = "ordersPage" };
            importPage = new TabPage("Import") { Name = "importPage" };
            settingsPage = new TabPage("Settings") { Name = "settingsPage" };
            newOrderGroup = new GroupBox();
            customerLabel = new Label();
            productLabel = new Label();
            quantityLabel = new Label();
            errorLabel = new Label();
            customerText = new TextBox();
            productCombo = new ComboBox();
            quantityUpDown = new NumericUpDown();
            expressCheck = new CheckBox();
            addButton = new Button();
            ordersGrid = new DataGridView();
            importLabel = new Label();
            importButton = new Button();
            importProgress = new ProgressBar();
            importLog = new ListBox();
            roundingGroup = new GroupBox();
            roundNone = new RadioButton();
            roundUp = new RadioButton();
            confirmCheck = new CheckBox();
            apiKeyLabel = new Label();
            apiKeyText = new TextBox();
            status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            clockLabel = new ToolStripStatusLabel();
            clockTimer = new Timer();
            importTimer = new Timer();
            SuspendLayout();

            // menu
            menu.Name = "menu";
            fileMenu.DropDownItems.AddRange(new ToolStripItem[] { newOrderItem, new ToolStripSeparator(), exitItem });
            helpMenu.DropDownItems.Add(aboutItem);
            menu.Items.AddRange(new ToolStripItem[] { fileMenu, helpMenu });
            newOrderItem.Click += (s, e) => { customerText.Text = ""; quantityUpDown.Value = 1; expressCheck.Checked = false; SetStatus("New order"); };
            exitItem.Click += (s, e) => Close();
            aboutItem.Click += (s, e) => MessageBox.Show(this, "Legacy Orders 1.0\nA WinForms app served by XUL-J.", "About");

            // tool strip
            tools.Name = "tools";
            tools.Items.AddRange(new ToolStripItem[] { deleteLastButton, clearButton, new ToolStripSeparator(), new ToolStripLabel("Currency:"), currencyBox });
            deleteLastButton.Click += (s, e) => DeleteLast();
            clearButton.Click += (s, e) =>
            {
                if (confirmCheck.Checked && MessageBox.Show(this, "Delete all orders?", "Confirm", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                ordersGrid.Rows.Clear();
                SetStatus("All orders cleared");
            };
            currencyBox.SelectedIndexChanged += (s, e) => RecomputeTotals();

            // new order group (absolute layout, as the designer writes it)
            newOrderGroup.Name = "newOrderGroup";
            newOrderGroup.Text = "New order";
            newOrderGroup.Dock = DockStyle.Top;
            newOrderGroup.Height = 130;
            customerLabel.Name = "customerLabel"; customerLabel.Text = "&Customer:"; customerLabel.Location = new Point(12, 26); customerLabel.AutoSize = true;
            customerText.Name = "customerText"; customerText.Location = new Point(90, 23); customerText.Size = new Size(260, 20);
            customerText.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            productLabel.Name = "productLabel"; productLabel.Text = "Product:"; productLabel.Location = new Point(12, 54); productLabel.AutoSize = true;
            productCombo.Name = "productCombo"; productCombo.Location = new Point(90, 51); productCombo.Size = new Size(140, 21); productCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            quantityLabel.Name = "quantityLabel"; quantityLabel.Text = "Qty:"; quantityLabel.Location = new Point(245, 54); quantityLabel.AutoSize = true;
            quantityUpDown.Name = "quantityUpDown"; quantityUpDown.Location = new Point(280, 51); quantityUpDown.Size = new Size(70, 20);
            quantityUpDown.Minimum = 1; quantityUpDown.Maximum = 999; quantityUpDown.Value = 1;
            expressCheck.Name = "expressCheck"; expressCheck.Text = "E&xpress shipping (+5.00)"; expressCheck.Location = new Point(90, 82); expressCheck.AutoSize = true;
            addButton.Name = "addButton"; addButton.Text = "&Add order"; addButton.Location = new Point(270, 80); addButton.Size = new Size(80, 25);
            addButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            addButton.Click += (s, e) => AddOrder();
            errorLabel.Name = "errorLabel"; errorLabel.ForeColor = Color.Firebrick; errorLabel.Location = new Point(12, 108); errorLabel.AutoSize = true;
            newOrderGroup.Controls.AddRange(new Control[] { customerLabel, customerText, productLabel, productCombo, quantityLabel, quantityUpDown, expressCheck, addButton, errorLabel });

            // grid
            ordersGrid.Name = "ordersGrid";
            ordersGrid.Dock = DockStyle.Fill;
            ordersGrid.AllowUserToAddRows = false;
            ordersGrid.ReadOnly = true;
            ordersGrid.Columns.Add("order", "Order #");
            ordersGrid.Columns.Add("customer", "Customer");
            ordersGrid.Columns.Add("product", "Product");
            ordersGrid.Columns.Add("qty", "Qty");
            ordersGrid.Columns.Add("express", "Express");
            ordersGrid.Columns.Add("total", "Total");
            ordersGrid.Columns["customer"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            ordersPage.Controls.Add(ordersGrid);
            ordersPage.Controls.Add(newOrderGroup); // added last = docked first, so the group sits on top

            // import page
            importLabel.Name = "importLabel"; importLabel.Text = "Simulates a slow batch import from the old mainframe export."; importLabel.Location = new Point(12, 14); importLabel.AutoSize = true;
            importButton.Name = "importButton"; importButton.Text = "Start &import"; importButton.Location = new Point(12, 40); importButton.Size = new Size(100, 25);
            importButton.Click += (s, e) => StartImport();
            importProgress.Name = "importProgress"; importProgress.Location = new Point(120, 42); importProgress.Size = new Size(230, 20); importProgress.Maximum = 25;
            importProgress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            importLog.Name = "importLog"; importLog.Location = new Point(12, 74); importLog.Size = new Size(338, 150);
            importLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            importPage.Controls.AddRange(new Control[] { importLabel, importButton, importProgress, importLog });
            importTimer.Interval = 120;
            importTimer.Tick += (s, e) => ImportStep();

            // settings page
            roundingGroup.Name = "roundingGroup"; roundingGroup.Text = "Rounding"; roundingGroup.Location = new Point(12, 12); roundingGroup.Size = new Size(338, 76);
            roundingGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            roundNone.Name = "roundNone"; roundNone.Text = "Exact cents"; roundNone.Location = new Point(12, 22); roundNone.AutoSize = true; roundNone.Checked = true;
            roundUp.Name = "roundUp"; roundUp.Text = "Round totals up to whole units"; roundUp.Location = new Point(12, 46); roundUp.AutoSize = true;
            roundUp.CheckedChanged += (s, e) => RecomputeTotals();
            roundingGroup.Controls.AddRange(new Control[] { roundNone, roundUp });
            confirmCheck.Name = "confirmCheck"; confirmCheck.Text = "Confirm before clearing all orders"; confirmCheck.Location = new Point(12, 100); confirmCheck.AutoSize = true; confirmCheck.Checked = true;
            apiKeyLabel.Name = "apiKeyLabel"; apiKeyLabel.Text = "API key:"; apiKeyLabel.Location = new Point(12, 132); apiKeyLabel.AutoSize = true;
            apiKeyText.Name = "apiKeyText"; apiKeyText.Location = new Point(90, 129); apiKeyText.Size = new Size(260, 20); apiKeyText.PasswordChar = '•';
            settingsPage.Controls.AddRange(new Control[] { roundingGroup, confirmCheck, apiKeyLabel, apiKeyText });

            tabs.Name = "tabs";
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.AddRange(new[] { ordersPage, importPage, settingsPage });

            // status strip
            status.Name = "status";
            statusLabel.Name = "statusLabel"; statusLabel.Spring = true; statusLabel.TextAlign = ContentAlignment.MiddleLeft; statusLabel.Text = "Ready";
            clockLabel.Name = "clockLabel";
            status.Items.AddRange(new ToolStripItem[] { statusLabel, clockLabel });
            clockTimer.Interval = 1000;
            clockTimer.Tick += (s, e) => clockLabel.Text = DateTime.Now.ToString("HH:mm:ss");

            // form
            AcceptButton = addButton;
            ClientSize = new Size(380, 420);
            Text = "Legacy Orders";
            Name = "MainForm";
            Controls.Add(tabs);
            Controls.Add(tools);
            Controls.Add(status);
            Controls.Add(menu);
            MainMenuStrip = menu;
            Load += (s, e) => { clockTimer.Start(); clockLabel.Text = DateTime.Now.ToString("HH:mm:ss"); };
            ResumeLayout(false);
            PerformLayout();
        }

        void AddOrder()
        {
            if (customerText.Text.Trim().Length == 0)
            {
                errorLabel.Text = "Customer is required.";
                SetStatus("Order not added");
                return;
            }
            errorLabel.Text = "";
            int p = productCombo.SelectedIndex;
            int qty = (int)quantityUpDown.Value;
            ordersGrid.Rows.Add(nextOrder++, customerText.Text.Trim(), Products[p], qty, expressCheck.Checked ? "yes" : "", "");
            RecomputeTotals();
            SetStatus("Added order for " + customerText.Text.Trim());
            customerText.Text = "";
        }

        void DeleteLast()
        {
            if (ordersGrid.Rows.Count == 0) { SetStatus("Nothing to delete"); return; }
            ordersGrid.Rows.RemoveAt(ordersGrid.Rows.Count - 1);
            SetStatus("Deleted last order");
        }

        void RecomputeTotals()
        {
            string currency = currencyBox.SelectedItem as string ?? "EUR";
            foreach (DataGridViewRow row in ordersGrid.Rows)
            {
                int p = Array.IndexOf(Products, row.Cells["product"].Value);
                decimal total = Prices[p] * Convert.ToInt32(row.Cells["qty"].Value) + ((string)row.Cells["express"].Value == "yes" ? 5m : 0m);
                if (roundUp.Checked) total = Math.Ceiling(total);
                row.Cells["total"].Value = total.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency;
            }
        }

        void StartImport()
        {
            importButton.Enabled = false;
            importProgress.Value = 0;
            importLog.Items.Clear();
            importLeft = importProgress.Maximum;
            importTimer.Start();
            SetStatus("Importing…");
        }

        void ImportStep()
        {
            var rnd = new Random(importLeft * 7919);
            string customer = new[] { "ACME", "Globex", "Initech", "Umbrella", "Hooli" }[rnd.Next(5)];
            int p = rnd.Next(Products.Length);
            ordersGrid.Rows.Add(nextOrder++, customer, Products[p], rnd.Next(1, 20), rnd.Next(3) == 0 ? "yes" : "", "");
            importLog.Items.Add("imported order for " + customer);
            importProgress.Value++;
            if (--importLeft == 0)
            {
                importTimer.Stop();
                importButton.Enabled = true;
                RecomputeTotals();
                SetStatus("Import finished: " + importProgress.Maximum + " orders");
            }
        }

        void SetStatus(string text) => statusLabel.Text = text;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }
}

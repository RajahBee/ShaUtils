using System.Windows;

namespace ShaUtils
{
    public enum ComparisonType
    {
        NamesSizesAndDates = 0,
        Crc64 = 1,
        Sha256 = 2
    }

    public enum Sha256Action
    {
        CompareIncludeSha = 0,
        CompareExcludeSha = 1,
        UseOrCreateSha = 2
    }

    public enum Sha256FileOption
    {
        UseExisting = 0,
        OverwriteHidden = 1,
        OverwriteVisible = 2
    }

    public partial class CompareFoldersDialog : Window
    {
        public ComparisonType SelectedComparisonType { get; private set; } = ComparisonType.NamesSizesAndDates;
        public Sha256Action SelectedSha256Action { get; private set; } = Sha256Action.CompareIncludeSha;
        public Sha256FileOption SelectedSha256FileOption { get; private set; } = Sha256FileOption.UseExisting;

        public CompareFoldersDialog(string folderAPath, string folderBPath)
        {
            InitializeComponent();
            FolderAPathTextBlock.Text = folderAPath;
            FolderBPathTextBlock.Text = folderBPath;
        }

        private void ComparisonTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Ensure the panel is initialized before accessing it to prevent NullReferenceException
            if (Sha256OptionsPanel == null) return;

            // Index 2 is SHA256 in the ComboBox items
            if (ComparisonTypeComboBox.SelectedIndex == 2)
            {
                Sha256OptionsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                Sha256OptionsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void Sha256ActionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (Sha256FileOptionsRow == null || Sha256FileOptionsComboBox == null) return;

            if (Sha256ActionComboBox.SelectedIndex == 2) // use or create .sha256 files and compare
            {
                Sha256FileOptionsRow.Visibility = Visibility.Visible;
                Sha256FileOptionsComboBox.Items.Clear();
                Sha256FileOptionsComboBox.Items.Add("use existing .sha256 files for compare");
                Sha256FileOptionsComboBox.Items.Add("create hidden .sha256 files, overwriting existing .sha256 files");
                Sha256FileOptionsComboBox.Items.Add("create .sha256 files, overwriting existing .sha256 files");
                Sha256FileOptionsComboBox.SelectedIndex = 0;
            }
            else
            {
                Sha256FileOptionsRow.Visibility = Visibility.Collapsed;
            }
        }

        private void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedComparisonType = (ComparisonType)ComparisonTypeComboBox.SelectedIndex;
            SelectedSha256Action = (Sha256Action)Sha256ActionComboBox.SelectedIndex;

            if (SelectedSha256Action == Sha256Action.UseOrCreateSha)
            {
                SelectedSha256FileOption = (Sha256FileOption)Sha256FileOptionsComboBox.SelectedIndex;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

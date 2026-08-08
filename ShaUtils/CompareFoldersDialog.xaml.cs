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
        CompareOnly = 0,
        CreateVisibleAndCompare = 1,
        CreateHiddenAndCompare = 2
    }

    public partial class CompareFoldersDialog : Window
    {
        public ComparisonType SelectedComparisonType { get; private set; } = ComparisonType.NamesSizesAndDates;
        public Sha256Action SelectedSha256Action { get; private set; } = Sha256Action.CompareOnly;

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

        private void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedComparisonType = (ComparisonType)ComparisonTypeComboBox.SelectedIndex;
            SelectedSha256Action = (Sha256Action)Sha256ActionComboBox.SelectedIndex;
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

using System.Windows;

namespace ShaUtils
{
    public enum ComparisonType
    {
        NamesAndCount = 0,
        NamesSizesAndDates = 1,
        Crc32 = 2,
        Sha256 = 3
    }

    public partial class CompareFoldersDialog : Window
    {
        public ComparisonType SelectedComparisonType { get; private set; } = ComparisonType.NamesAndCount;

        public CompareFoldersDialog(string folderAPath, string folderBPath)
        {
            InitializeComponent();
            FolderAPathTextBlock.Text = folderAPath;
            FolderBPathTextBlock.Text = folderBPath;
        }

        private void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedComparisonType = (ComparisonType)ComparisonTypeComboBox.SelectedIndex;
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

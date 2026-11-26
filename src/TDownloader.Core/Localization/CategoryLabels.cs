using TDownloader.Core.Downloads;
namespace TDownloader.Core.Localization;
public static class CategoryLabels
{
    public static string Display(string id) => id switch
    {
        Categories.Compressed => Strings.CategoryCompressed,
        Categories.Documents => Strings.CategoryDocuments,
        Categories.Music => Strings.CategoryMusic,
        Categories.Programs => Strings.CategoryPrograms,
        Categories.Video => Strings.CategoryVideo,
        Categories.Pictures => Strings.CategoryPictures,
        Categories.General => Strings.CategoryGeneral,
        _ => id
    };
}

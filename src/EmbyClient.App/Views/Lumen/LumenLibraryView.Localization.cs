using EmbyClient.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    static LumenLibraryView() => LumenText.Register(new Dictionary<string, string>
    {
        ["Person"] = "\u4eba\u7269",
        ["Works"] = "\u4f5c\u54c1",
        ["Read more"] = "\u5c55\u5f00",
        ["Show less"] = "\u6536\u8d77",
        ["Previous"] = "\u4e0a\u4e00\u9875",
        ["Next"] = "\u4e0b\u4e00\u9875",
        ["Smaller posters"] = "\u7f29\u5c0f\u6d77\u62a5",
        ["Larger posters"] = "\u653e\u5927\u6d77\u62a5",
        ["Watch status"] = "\u89c2\u770b\u72b6\u6001",
        ["Clear filters"] = "\u6e05\u9664\u7b5b\u9009",
        ["{0} items"] = "{0} \u90e8",
        ["{0} works"] = "{0} \u90e8\u4f5c\u54c1",
        ["{0} columns"] = "{0} \u5217",
        ["{0} h {1} min"] = "{0} \u5c0f\u65f6 {1} \u5206\u949f",
        ["Titles beginning with {0}"] = "\u4ee5 {0} \u5f00\u5934\u7684\u5185\u5bb9",
        ["Your library is empty"] = "\u5a92\u4f53\u5e93\u6682\u65e0\u5185\u5bb9",
        ["No favorites yet"] = "\u6682\u65e0\u6536\u85cf",
        ["TV shows"] = "\u5267\u96c6",
        ["Your library"] = "\u6211\u7684\u5a92\u4f53\u5e93",
        ["No items are available."] = "\u6682\u65e0\u53ef\u7528\u5185\u5bb9\u3002",
        ["Connect to a server to browse your media."] = "\u8bf7\u8fde\u63a5\u670d\u52a1\u5668\u4ee5\u67e5\u770b\u5a92\u4f53\u3002",
        ["Your home is ready for a first watch. Browse a media library to get started."] = "\u5f53\u524d\u5a92\u4f53\u5e93\u8fd8\u6ca1\u6709\u53ef\u663e\u793a\u7684\u5185\u5bb9\u3002",
        ["Nothing to continue yet. Start watching a movie or episode from a library."] = "\u6682\u65e0\u7ee7\u7eed\u89c2\u770b\u8bb0\u5f55\u3002",
        ["No next episodes are available. Your shows will appear here as you watch them."] = "\u6682\u65e0\u5f85\u64ad\u7684\u4e0b\u4e00\u96c6\u3002",
        ["No favorites yet. Open an item and choose Add favorite."] = "\u4f60\u7684\u6536\u85cf\u4e2d\u8fd8\u6ca1\u6709\u5185\u5bb9\u3002",
        ["Search your library using the search box in the sidebar."] = "\u641c\u7d22\u4f60\u7684\u5a92\u4f53\u5e93\u3002",
        ["No matching items. Try a different title or a shorter search."] = "\u6ca1\u6709\u5339\u914d\u7684\u5185\u5bb9\uff0c\u8bf7\u5c1d\u8bd5\u5176\u4ed6\u540d\u79f0\u3002",
        ["No items are available in this collection."] = "\u6b64\u5408\u96c6\u6682\u65e0\u53ef\u7528\u5185\u5bb9\u3002",
        ["No items match this watch status. Clear the filter to see all available items."] = "\u6ca1\u6709\u7b26\u5408\u5f53\u524d\u89c2\u770b\u72b6\u6001\u7684\u5185\u5bb9\u3002",
        ["People could not be loaded. Try searching again."] = "\u4eba\u7269\u641c\u7d22\u672a\u80fd\u52a0\u8f7d\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["People could not be refreshed. Previously loaded results are still shown."] = "\u4eba\u7269\u641c\u7d22\u672a\u80fd\u5237\u65b0\uff0c\u4ecd\u663e\u793a\u5df2\u52a0\u8f7d\u7684\u7ed3\u679c\u3002",
        ["No biography has been provided by the server."] = "\u670d\u52a1\u5668\u672a\u63d0\u4f9b\u4eba\u7269\u7b80\u4ecb\u3002",
        ["Unable to load person information. Try again."] = "\u4eba\u7269\u4fe1\u606f\u672a\u80fd\u52a0\u8f7d\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["Unable to load library works. Try again."] = "\u4f5c\u54c1\u672a\u80fd\u52a0\u8f7d\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["This content is restricted by the account's parental controls."] = "\u6b64\u5185\u5bb9\u53d7\u8d26\u6237\u5bb6\u957f\u63a7\u5236\u9650\u5236\u3002",
        ["Your session has expired. Sign out and connect again."] = "\u4f1a\u8bdd\u5df2\u8fc7\u671f\uff0c\u8bf7\u91cd\u65b0\u767b\u5f55\u3002",
        ["This account is not allowed to access this content."] = "\u6b64\u8d26\u6237\u4e0d\u5141\u8bb8\u8bbf\u95ee\u6b64\u5185\u5bb9\u3002",
        ["This content is no longer available on the server. Refresh the library."] = "\u6b64\u5185\u5bb9\u5df2\u4ece\u670d\u52a1\u5668\u79fb\u9664\uff0c\u8bf7\u5237\u65b0\u5a92\u4f53\u5e93\u3002",
        ["The server could not complete the request. Try refreshing this view."] = "\u670d\u52a1\u5668\u672a\u80fd\u5b8c\u6210\u8bf7\u6c42\uff0c\u8bf7\u5237\u65b0\u91cd\u8bd5\u3002",
        ["The server took too long to respond. Check the connection and try again."] = "\u670d\u52a1\u5668\u54cd\u5e94\u8d85\u65f6\uff0c\u8bf7\u68c0\u67e5\u7f51\u7edc\u540e\u91cd\u8bd5\u3002",
        ["The server could not be reached. Check the connection and try again."] = "\u65e0\u6cd5\u8fde\u63a5\u670d\u52a1\u5668\uff0c\u8bf7\u68c0\u67e5\u7f51\u7edc\u540e\u91cd\u8bd5\u3002",
        ["The server returned an unsupported response. Try refreshing this view."] = "\u670d\u52a1\u5668\u8fd4\u56de\u4e86\u65e0\u6cd5\u8bc6\u522b\u7684\u54cd\u5e94\uff0c\u8bf7\u5237\u65b0\u91cd\u8bd5\u3002",
        ["Some home sections could not be loaded. Refresh to try again."] = "\u90e8\u5206\u9996\u9875\u5185\u5bb9\u672a\u80fd\u52a0\u8f7d\uff0c\u8bf7\u5237\u65b0\u91cd\u8bd5\u3002",
        ["Some home sections could not be refreshed. Previously loaded content is still shown."] = "\u90e8\u5206\u9996\u9875\u5185\u5bb9\u672a\u80fd\u5237\u65b0\uff0c\u4ecd\u663e\u793a\u5df2\u52a0\u8f7d\u7684\u5185\u5bb9\u3002"
    });

    private static TextBlock Label(string key, double size = 14, bool serif = false) => LumenUi.Text(LumenText.Get(key), size, serif);
}

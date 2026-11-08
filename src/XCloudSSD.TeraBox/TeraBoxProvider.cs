namespace XCloudSSD.TeraBox;

/// <summary>Public TeraBox URLs used by the app.</summary>
public static class TeraBoxInfo
{
    public const string OfficialSite = "https://www.terabox.com/";
    public const string LoginUrl = "https://www.terabox.com/";
    public const string CookieDomain = "https://www.terabox.com";

    public const string Disclosure =
        "TeraBox has no public API for apps. XCloudSSD signs in through TeraBox's own website inside the app and then " +
        "uses the same web interface the TeraBox website uses (the method used by open-source tools such as AList). " +
        "Your session cookie is stored encrypted with Windows DPAPI and only sent to terabox.com. TeraBox can change " +
        "this interface at any time, and automated access may not be allowed by TeraBox's terms — use it with your own account at your own risk.";
}

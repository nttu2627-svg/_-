// Scripts\Utility\LocationNameLocalizer.cs

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// 提供地點名稱的本地化與清理功能，
/// 將原始的地點代號轉換成對玩家友善的中文顯示文字。
/// </summary>
public static class LocationNameLocalizer
{
    private static readonly Dictionary<string, string> LocationAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "APARTMENT", "公寓" },
        { "APARTMENT_F1", "公寓一樓" },
        { "APARTMENT_F2", "公寓二樓" },
        { "REST", "餐廳" },
        { "RESTAURANT", "餐廳" },
        { "SCHOOL", "學校" },
        { "GYM", "健身房" },
        { "SUPER", "超市" },
        { "SUPERMARKET", "超市" },
        { "SUBWAY", "地鐵" },
        { "EXTERIOR", "室外" },
        { "PARK", "公園" },
        { "HOSPITAL", "醫院" },
        { "CAFE", "咖啡店" },
        { "OFFICE", "辦公室" }
    };

    private static readonly Regex BoundsSuffixRegex = new Regex("Bounds$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 將原始地點名稱轉換為對玩家顯示的中文名稱。
    /// </summary>
    public static string ToDisplayName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "未知地點";
        }

        string normalized = Normalize(rawName);

        if (LocationAliasMap.TryGetValue(normalized.ToUpperInvariant(), out string mapped))
        {
            return mapped;
        }

        // 若名稱本身包含中文或其他可顯示內容，則去除多餘符號後直接回傳。
        string cleaned = CleanupDecorations(normalized);
        if (!string.IsNullOrEmpty(cleaned))
        {
            return cleaned;
        }

        return rawName.Trim();
    }

    /// <summary>
    /// 嘗試擴充地點的替代名稱集合，避免重複並確保包含中文顯示名稱。
    /// </summary>
    public static void AppendDisplayAlias(HashSet<string> aliasSet, string rawName)
    {
        if (aliasSet == null) return;
        string display = ToDisplayName(rawName);
        if (!string.IsNullOrEmpty(display))
        {
            aliasSet.Add(display);
        }
    }

    private static string Normalize(string name)
    {
        string trimmed = name.Trim();
        trimmed = trimmed.Replace("\\", "/");
        int lastSlash = trimmed.LastIndexOf('/') + 1;
        if (lastSlash > 0 && lastSlash < trimmed.Length)
        {
            trimmed = trimmed.Substring(lastSlash);
        }

        trimmed = BoundsSuffixRegex.Replace(trimmed, string.Empty);
        return trimmed;
    }

    private static string CleanupDecorations(string value)
    {
        string withoutUnderscore = value.Replace("__", "_");
        withoutUnderscore = withoutUnderscore.Replace('_', ' ');
        withoutUnderscore = withoutUnderscore.Replace("  ", " ").Trim();

        if (string.IsNullOrEmpty(withoutUnderscore))
        {
            return string.Empty;
        }

        // 將常見的後綴轉換為更自然的呈現方式
        withoutUnderscore = withoutUnderscore
            .Replace(" 室內", "（室內）")
            .Replace(" 室外", "（室外）")
            .Replace(" 頂樓", "頂樓")
            .Replace(" 一樓", "一樓")
            .Replace(" 二樓", "二樓");

        return withoutUnderscore;
    }
}
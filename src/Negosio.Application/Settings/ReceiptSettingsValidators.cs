using System;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using Negosio.Domain.Enums;

namespace Negosio.Application.Settings;

public static class ReceiptText
{
    private static readonly Regex SpaceRuns = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex NewlineRuns = new(@"\n{3,}", RegexOptions.Compiled);

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '\n' || !char.IsControl(ch)) sb.Append(ch);
        }

        var collapsed = NewlineRuns.Replace(SpaceRuns.Replace(sb.ToString(), " "), "\n\n");
        // trim each line's trailing spaces, then trim the whole thing
        collapsed = string.Join('\n', collapsed.Split('\n').Select(l => l.TrimEnd()));
        collapsed = collapsed.Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    public static int LineCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n') + 1;
}

public sealed class UpdateReceiptSettingsRequestValidator : AbstractValidator<UpdateReceiptSettingsRequest>
{
    public UpdateReceiptSettingsRequestValidator()
    {
        RuleFor(x => x.Width).IsInEnum();

        Text(x => x.SalesHeaderText, nameof(UpdateReceiptSettingsRequest.SalesHeaderText));
        Text(x => x.SalesFooterText, nameof(UpdateReceiptSettingsRequest.SalesFooterText));
        Text(x => x.DeliveryHeaderText, nameof(UpdateReceiptSettingsRequest.DeliveryHeaderText));
        Text(x => x.DeliveryFooterText, nameof(UpdateReceiptSettingsRequest.DeliveryFooterText));
    }

    private void Text(Expression<Func<UpdateReceiptSettingsRequest, string?>> selector, string name)
    {
        RuleFor(selector).Custom((value, ctx) =>
        {
            var normalized = ReceiptText.Normalize(value);
            if (normalized is null) return;
            if (normalized.Length > 500)
                ctx.AddFailure(name, "Keep this under 500 characters.");
            if (ReceiptText.LineCount(normalized) > 6)
                ctx.AddFailure(name, "Keep this to 6 lines or fewer.");
        });
    }
}

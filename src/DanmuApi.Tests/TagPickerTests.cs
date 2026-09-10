using Avalonia.Headless.XUnit;
using DanmuApi.App.Controls;

namespace DanmuApi.Tests;

public sealed class TagPickerTests
{
    [AvaloniaFact]
    public void CandidateCanBeAddedAndRemovedWithoutListBoxSelectionState()
    {
        var picker = new TagPicker(["bilibili", "dandan", "animeko"]);

        Assert.True(picker.AddValue("bilibili"));
        Assert.False(picker.AddValue("bilibili"));
        Assert.Equal(["bilibili"], picker.Values);

        picker.RemoveValue("bilibili");

        Assert.Empty(picker.Values);
    }

    [AvaloniaFact]
    public void OrderedSelectionMovesTheActiveChip()
    {
        var picker = new TagPicker(["bilibili", "dandan", "animeko"]);
        picker.AddValue("bilibili");
        picker.AddValue("dandan");
        picker.AddValue("animeko");

        picker.MoveActive(-1);

        Assert.Equal(["bilibili", "animeko", "dandan"], picker.Values);
    }

    [AvaloniaFact]
    public void CompositeValueComparesTokensAndRejectsDuplicates()
    {
        var picker = new TagPicker(
            ["bilibili", "dandan", "animeko"],
            compareTokens: true,
            combineStaging: true,
            useStaging: true,
            allowSelectedReuse: true,
            allowCompositeValues: true);

        Assert.True(picker.AddStagedValue("bilibili"));
        Assert.True(picker.AddStagedValue("dandan"));
        Assert.False(picker.AddStagedValue("dandan"));
        Assert.False(picker.AddValue("bilibili&bilibili"));
        picker.AddValue("bilibili&dandan");

        Assert.False(picker.AddValue("dandan&bilibili"));
        Assert.Equal(["bilibili&dandan"], picker.Values);
    }

    [AvaloniaFact]
    public void UnknownCandidateIsRejected()
    {
        var picker = new TagPicker(["bilibili"]);

        Assert.False(picker.AddValue("unknown"));
        Assert.False(picker.AddValue("bilibili&unknown"));
        Assert.Empty(picker.Values);
    }

    [AvaloniaFact]
    public void StagingAllowsSameSourceInDifferentGroups()
    {
        var picker = new TagPicker(
            ["bilibili", "dandan", "animeko"],
            compareTokens: true,
            combineStaging: true,
            useStaging: true,
            allowSelectedReuse: true,
            allowCompositeValues: true);

        picker.AddStagedValue("bilibili");
        picker.AddStagedValue("dandan");
        picker.AddValue("bilibili&dandan");
        picker.ClearStaging();
        picker.AddStagedValue("bilibili");
        picker.AddStagedValue("animeko");
        picker.AddValue("bilibili&animeko");

        Assert.Equal(["bilibili&dandan", "bilibili&animeko"], picker.Values);
    }
}

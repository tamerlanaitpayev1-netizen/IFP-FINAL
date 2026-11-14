using ArenaQuiz.Client.Components;
using ArenaQuiz.Shared.Models;
using Bunit;

namespace ArenaQuiz.Tests;

public sealed class BlazorComponentsBunitTests : BunitContext
{
    [Fact]
    public void QuestionCard_RendersOptionsAndInvokesEventCallbackOnClick()
    {
        var question = new LiveQuestionViewDto(
            0,
            5,
            "What makes C# records ideal for SignalR DTOs?",
            null,
            ["Mutable globals", "Value equality and immutability", "Implicit goto", "Manual pointers"],
            20,
            1000,
            GameMode.ClassicSpeed);

        int? selected = null;
        var cut = Render<QuestionCard>(p => p
            .Add(c => c.Question, question)
            .Add(c => c.OnOptionSelected, idx => selected = idx));

        Assert.Contains("Value equality and immutability", cut.Markup);
        cut.FindAll("button.aq-option-btn")[1].Click();
        Assert.Equal(1, selected);
    }

    [Fact]
    public void QuestionCard_RevealMode_ShowsCorrectStylingAndExplanation()
    {
        var question = new LiveQuestionViewDto(0, 3, "Q?", null, ["Yes", "No"], 15, 1000, GameMode.ClassicSpeed);
        var reveal = new QuestionRevealDto(0, 0, "Zero-allocation spans avoid heap pressure.", [4, 1], [], [], []);

        var cut = Render<QuestionCard>(p => p
            .Add(c => c.Question, question)
            .Add(c => c.Reveal, reveal)
            .Add(c => c.SelectedOptionIndex, 1));

        Assert.Contains("aq-option-correct", cut.Markup);
        Assert.Contains("aq-option-wrong", cut.Markup);
        Assert.Contains("Zero-allocation spans avoid heap pressure.", cut.Markup);
    }

    [Fact]
    public void RoundTimer_DisplaysRemainingSecondsAndUrgentState()
    {
        var cut = Render<RoundTimer>(p => p
            .Add(c => c.RemainingSeconds, 3)
            .Add(c => c.TotalSeconds, 20));

        Assert.Contains("3 s", cut.Markup);
        Assert.Contains("is-urgent", cut.Markup);
    }

    [Fact]
    public void RoundTimer_PausedState_RendersPausedLabelAndClass()
    {
        var cut = Render<RoundTimer>(p => p
            .Add(c => c.RemainingSeconds, 12)
            .Add(c => c.TotalSeconds, 20)
            .Add(c => c.IsPaused, true));

        Assert.Contains("is-paused", cut.Markup);
        Assert.Contains("Paused", cut.Markup);
    }

    [Fact]
    public void LiveLeaderboard_TogglesBetweenPlayersAndTeams()
    {
        IReadOnlyList<PlayerStandingDto> players =
        [
            new("p1", "Nurlan", TeamSide.Alpha, 1850, 950, 2, 2, 2, 1100, true, false, 1)
        ];
        IReadOnlyList<TeamStandingDto> teams =
        [
            new(TeamSide.Alpha, "Team Cobalt", 1850, 1, 1850)
        ];

        var cut = Render<LiveLeaderboard>(p => p
            .Add(c => c.Players, players)
            .Add(c => c.Teams, teams)
            .Add(c => c.Mode, GameMode.TeamBattle));

        Assert.Contains("Nurlan", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent.Contains("Teams")).Click();
        Assert.Contains("Team Cobalt", cut.Markup);
    }

    [Fact]
    public void PlayerRoster_HostCanKickPlayerViaEventCallback()
    {
        string? kickedId = null;
        IReadOnlyList<PlayerStandingDto> players =
        [
            new("id-77", "Kairat", TeamSide.None, 0, 0, 0, 0, 0, 0, true, false, 1)
        ];

        var cut = Render<PlayerRoster>(p => p
            .Add(c => c.Players, players)
            .Add(c => c.IsHost, true)
            .Add(c => c.OnRemovePlayer, id => kickedId = id));

        cut.Find("button").Click();
        Assert.Equal("id-77", kickedId);
    }

    [Fact]
    public void TemplatedMetricGrid_RendersGenericHeaderAndRowTemplates()
    {
        var cut = Render<TemplatedMetricGrid<string>>(p => p
            .Add(c => c.Items, new[] { "Record-A", "Record-B" })
            .Add(c => c.HeaderTemplate, builder => builder.AddContent(0, "Header"))
            .Add(c => c.RowTemplate, item => builder => builder.AddContent(0, $"Row:{item}")));

        Assert.Contains("Header", cut.Markup);
        Assert.Contains("Row:Record-A", cut.Markup);
        Assert.Contains("Row:Record-B", cut.Markup);
    }

    [Fact]
    public void QrCodeSvg_RendersValidSvgMatrixAndCaption()
    {
        var cut = Render<QrCodeSvg>(p => p
            .Add(c => c.Payload, "https://localhost:5152/play?pin=AQ4829")
            .Add(c => c.PixelSize, 156)
            .Add(c => c.Caption, "AQ4829"));

        var svg = cut.Find("svg");
        Assert.Equal("156", svg.GetAttribute("width"));
        Assert.NotEmpty(cut.FindAll("svg rect"));
        Assert.Contains("AQ4829", cut.Markup);
    }
}

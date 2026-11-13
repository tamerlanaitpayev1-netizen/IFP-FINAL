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
            "Which keyword copies an immutable C# record?",
            "var next = p with { TotalScore = 100 };",
            ["clone", "with", "mutate", "copy"],
            20,
            1000,
            GameMode.ClassicSpeed);

        int? clickedIndex = null;
        var cut = Render<QuestionCard>(parameters => parameters
            .Add(p => p.Question, question)
            .Add(p => p.OnOptionSelected, idx => clickedIndex = idx));

        var buttons = cut.FindAll("button.aq-option-btn");
        Assert.Equal(4, buttons.Count);
        Assert.Contains("with", buttons[1].TextContent);

        buttons[1].Click();
        Assert.Equal(1, clickedIndex);
    }

    [Fact]
    public void QuestionCard_RevealMode_ShowsCorrectStylingAndExplanation()
    {
        var question = new LiveQuestionViewDto(
            0, 5, "Prompt", null, ["A", "B", "C", "D"], 20, 1000, GameMode.ClassicSpeed);
        var reveal = new QuestionRevealDto(
            0, 2, "C is correct because of O(1) lookup.", [1, 0, 3, 0], [], [], []);

        var cut = Render<QuestionCard>(parameters => parameters
            .Add(p => p.Question, question)
            .Add(p => p.SelectedOptionIndex, 0)
            .Add(p => p.Reveal, reveal));

        var buttons = cut.FindAll("button.aq-option-btn");
        Assert.Contains("aq-option-wrong", buttons[0].ClassName);
        Assert.Contains("aq-option-correct", buttons[2].ClassName);
        Assert.Contains("O(1) lookup", cut.Markup);
    }

    [Fact]
    public void RoundTimer_DisplaysRemainingSecondsAndUrgentState()
    {
        var cut = Render<RoundTimer>(parameters => parameters
            .Add(p => p.RemainingSeconds, 4)
            .Add(p => p.TotalSeconds, 20));

        Assert.Contains("4 s", cut.Markup);
        Assert.Contains("var(--crimson-bg)", cut.Markup);
    }

    [Fact]
    public void LiveLeaderboard_TogglesBetweenPlayersAndTeams()
    {
        var players = new[]
        {
            new PlayerStandingDto("p1", "Tamerlan", TeamSide.Alpha, 1500, 500, 2, 2, 2, 1200, true, false, 1),
            new PlayerStandingDto("p2", "Dias", TeamSide.Beta, 1200, 400, 1, 1, 2, 1600, true, false, 2)
        };
        var teams = new[]
        {
            new TeamStandingDto(TeamSide.Alpha, "Team Alpha", 1500, 1, 1500),
            new TeamStandingDto(TeamSide.Beta, "Team Beta", 1200, 1, 1200)
        };

        var cut = Render<LiveLeaderboard>(parameters => parameters
            .Add(p => p.Players, players)
            .Add(p => p.Teams, teams)
            .Add(p => p.Mode, GameMode.TeamBattle)
            .Add(p => p.HighlightPlayerId, "p1"));

        Assert.Contains("Tamerlan", cut.Markup);
        Assert.Contains("is-self", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Contains("Teams")).Click();
        Assert.Contains("Team Alpha", cut.Markup);
        Assert.Contains("Team Beta", cut.Markup);
    }

    [Fact]
    public void PlayerRoster_HostCanKickPlayerViaEventCallback()
    {
        string? kickedId = null;
        var players = new[]
        {
            new PlayerStandingDto("p99", "GuestUser", TeamSide.None, 0, 0, 0, 0, 0, 0, true, false, 1)
        };

        var cut = Render<PlayerRoster>(parameters => parameters
            .Add(p => p.Players, players)
            .Add(p => p.IsHost, true)
            .Add(p => p.OnRemovePlayer, id => kickedId = id));

        cut.Find("button").Click();
        Assert.Equal("p99", kickedId);
    }

    [Fact]
    public void TemplatedMetricGrid_RendersGenericHeaderAndRowTemplates()
    {
        var items = new[] { "Session-A", "Session-B" };
        var cut = Render<TemplatedMetricGrid<string>>(parameters => parameters
            .Add(p => p.Items, items)
            .Add(p => p.HeaderTemplate, "<h2>Custom Header</h2>")
            .Add(p => p.RowTemplate, item => $"<div class='row-item'>{item}</div>"));

        Assert.Contains("Custom Header", cut.Markup);
        Assert.Equal(2, cut.FindAll(".row-item").Count);
    }
}


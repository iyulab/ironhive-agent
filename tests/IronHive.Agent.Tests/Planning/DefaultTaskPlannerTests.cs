using AwesomeAssertions;
using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Planning;
using IronHive.Agent.Tests.Mocks;

namespace IronHive.Agent.Tests.Planning;

public class DefaultTaskPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TaskPlan> PlanFrom(string json)
    {
        var mock = new MockChatClient().EnqueueResponse(json);
        return await new DefaultTaskPlanner(mock).CreatePlanAsync("organize the folder", new PlanningContext(), Ct);
    }

    [Fact]
    public async Task A_plan_numbered_from_zero_gets_one_index_per_step_and_its_dependencies_follow()
    {
        var plan = await PlanFrom("""
            {"steps":[
              {"index":0,"description":"List files","dependsOn":[]},
              {"index":1,"description":"Create folders","dependsOn":[0]},
              {"index":2,"description":"Move files","dependsOn":[1]}]}
            """);

        plan.Steps.Select(s => s.Index).Should().Equal(1, 2, 3);
        plan.Steps.Select(s => s.Description).Should().Equal("List files", "Create folders", "Move files");
        plan.Steps[1].DependsOn.Should().Equal(1); // "Create folders" depends on "List files"
        plan.Steps[2].DependsOn.Should().Equal(2); // "Move files" depends on "Create folders"
    }

    [Fact]
    public async Task A_plan_numbered_from_one_is_unchanged()
    {
        var plan = await PlanFrom("""
            {"steps":[
              {"index":1,"description":"A"},
              {"index":2,"description":"B","dependsOn":[1]}]}
            """);

        plan.Steps.Select(s => s.Index).Should().Equal(1, 2);
        plan.Steps[1].DependsOn.Should().Equal(1);
    }

    [Fact]
    public async Task Repeated_or_missing_numbers_are_numbered_by_position_and_unknown_dependencies_dropped()
    {
        var plan = await PlanFrom("""
            {"steps":[
              {"description":"no number"},
              {"index":5,"description":"five","dependsOn":[5, 9]},
              {"index":5,"description":"five again","dependsOn":[5]}]}
            """);

        plan.Steps.Select(s => s.Index).Should().Equal(1, 2, 3);
        plan.Steps[1].DependsOn.Should().BeEmpty();   // itself, and a number no step has
        plan.Steps[2].DependsOn.Should().Equal(2);    // a repeated number names its first step
    }
}

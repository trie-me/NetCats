using Microsoft.AspNetCore.Http.HttpResults;
using PurrfectSeat.Api;
using PurrfectSeat.Infrastructure;

namespace PurrfectSeat.Api.Tests;

public sealed class ApiSurfaceTests
{
    [Fact]
    public async Task Named_business_handler_returns_a_typed_performance_list()
    {
        var repository = new InMemoryPerformanceRepository();
        foreach (var performance in SeedData.CreateCatalogue())
        {
            repository.Seed(performance);
        }

        var response = await PurrfectSeatEndpoints.ListPerformances(repository, CancellationToken.None);

        Assert.IsType<Ok<IReadOnlyList<PurrfectSeat.Contracts.PerformanceSummaryResponse>>>(response);
        Assert.Equal(4, response.Value!.Count);
        Assert.Equal(response.Value.OrderBy(static performance => performance.StartsAt), response.Value);
    }

    [Fact]
    public void Box_office_and_control_room_assets_are_part_of_the_api_project()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/PurrfectSeat.Api/wwwroot"));

        Assert.True(File.Exists(Path.Combine(root, "index.html")));
        Assert.True(File.Exists(Path.Combine(root, "control-room.html")));
        Assert.True(File.Exists(Path.Combine(root, "js", "box-office.js")));
        Assert.True(File.Exists(Path.Combine(root, "js", "control-room.js")));
    }
}

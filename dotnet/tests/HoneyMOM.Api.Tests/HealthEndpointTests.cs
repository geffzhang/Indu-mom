using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task GetHealth_ReturnsOk()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }
}

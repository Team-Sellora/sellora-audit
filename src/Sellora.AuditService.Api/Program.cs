using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = (context, report) =>
        context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() })
});

await app.RunAsync();

public partial class Program
{
    protected Program()
    {
    }
}

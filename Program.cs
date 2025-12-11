using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------
// Register Services
// --------------------------------------------------

// Register DateTimeService as a Singleton 
builder.Services.AddSingleton<IDateTimeService, DateTimeService>();

// Add controllers (optional)
builder.Services.AddControllers();

var app = builder.Build();

// --------------------------------------------------
// Middleware Pipeline
// --------------------------------------------------

app.UseHttpsRedirection();
app.MapControllers();

// Test endpoint (optional)
app.MapGet("/test-date/{date}", (string date, IDateTimeService dtService) =>
{
    if (!dtService.TryParsePersianDate(date, out var result))
        return Results.BadRequest("Invalid date format");

    return Results.Ok(new
    {
        Input = date,
        Utc = result,
        Iso = result.ToString("yyyy-MM-ddTHH:mm:ssZ")
    });
});

app.Run();

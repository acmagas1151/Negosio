using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Negosio.Api.Authentication;
using Negosio.Api.Authorization;
using Negosio.Api.Middleware;
using Negosio.Application;
using Negosio.Application.Abstractions;
using Negosio.Infrastructure;
using Negosio.Infrastructure.Persistence;
using Negosio.Infrastructure.Tenancy;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddSingleton<IAppEnvironment, HostAppEnvironment>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.ConfigureOptions<ConfigureJwtBearerOptions>();

builder.Services.AddAuthorization(options => options.AddNegosioPolicies());

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<BranchAccessMiddleware>();
app.UseAuthorization();

app.MapControllers();

await ApplyMigrationsAsync(app);

app.Run();

static async Task ApplyMigrationsAsync(WebApplication app)
{
    if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await platform.Database.MigrateAsync();

    // Bring every already-provisioned tenant database up to the latest tenant schema.
    var provisioner = scope.ServiceProvider.GetRequiredService<ITenantDatabaseProvisioner>();
    var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
    var mappings = await platform.TenantDatabases.AsNoTracking().ToListAsync();
    foreach (var mapping in mappings)
    {
        var connectionString = provisioner.BuildConnectionString(mapping.ServerKey, mapping.DatabaseName);
        await using var tenantDb = factory.CreateForConnection(connectionString);
        await tenantDb.Database.MigrateAsync();
    }
}

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;

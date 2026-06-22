using SmartStudy.Web.Components;
using SmartStudy.Web.Services;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Configuration
    .AddJsonFile("smartstudy.agent.json", optional: false)
    .AddJsonFile("smartstudy.agent.Local.json", optional: true)
    .AddEnvironmentVariables(prefix: "SMARTSTUDY_");
builder.Services.AddSmartStudyAgent(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using System.ServiceModel.Syndication;
using System.Xml;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5260");

builder.Services.AddSingleton<IDbConnection>(sp =>
    new SqliteConnection("Data Source=Data/RssAtomReader.db"));
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});


var app = builder.Build();
app.UseSession();
app.UseDefaultFiles();
app.UseStaticFiles();

void InitializeDatabase(IDbConnection db)
{
    db.Execute(@"
        CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Email TEXT NOT NULL UNIQUE,
            Password TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Feeds (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FeedUrl TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS UserFeeds (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            FeedId INTEGER NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE,
            FOREIGN KEY (FeedId) REFERENCES Feeds(Id) ON DELETE CASCADE,
            UNIQUE (UserId, FeedId)
        );
    ");
}

using (var db = new SqliteConnection("Data Source=Data/RssAtomReader.db"))
{
    db.Open();
    InitializeDatabase(db);
}

// Registration Endpoint
app.MapPost("/register", async (HttpContext context) =>
{
    var email = context.Request.Form["email"].ToString();
    var password = context.Request.Form["password"].ToString();

    var db = context.RequestServices.GetRequiredService<IDbConnection>();

    var existingUser = await db.QuerySingleOrDefaultAsync("SELECT * FROM Users WHERE Email = @Email", new { Email = email });
    if (existingUser != null)
    {
        return Results.Ok("<p>This email is already registered.</p>");
    }

    var result = await db.ExecuteAsync("INSERT INTO Users (Email, Password) VALUES (@Email, @Password)", 
                                       new { Email = email, Password = password });
    return result > 0 ? Results.Ok("<p>Registration successful!</p>") : Results.Ok("<p>Registration failed.</p>");
});

// Login Endpoint
app.MapPost("/login", async (HttpContext context) =>
{
    var email = context.Request.Form["email"].ToString();
    var password = context.Request.Form["password"].ToString();

    var db = context.RequestServices.GetRequiredService<IDbConnection>();

    var user = await db.QuerySingleOrDefaultAsync("SELECT * FROM Users WHERE Email = @Email AND Password = @Password", 
                                                  new { Email = email, Password = password });

    if (user != null)
    {
        context.Session.SetInt32("userId", (int)user.Id);
        context.Response.Cookies.Append("userId", user.Id.ToString(), new CookieOptions
        {
            HttpOnly = true
        });
        
        // Return a success HTML snippet or a simple indicator
        return Results.Ok("<script>document.dispatchEvent(new Event('loginSuccess'));</script>");
    }
    return Results.Ok("<p>Invalid email or password.</p>");
});


// Logout Endpoint
app.MapPost("/logout", async (HttpContext context) =>
{
    context.Session.Remove("userId");
    context.Response.Cookies.Delete("userId");
    await context.Session.CommitAsync();  // Ensures session changes are saved
    return Results.Ok("<p>Logged out successfully.</p>");
});

// Add Feed Endpoint
app.MapPost("/addFeed", async (HttpContext context) =>
{
    if (!context.Request.Cookies.TryGetValue("userId", out string? userIdStr) || !int.TryParse(userIdStr, out int userId))
    {
        return Results.Ok("<p>Please log in to add feeds.</p>");
    }

    var feedUrl = context.Request.Form["feedUrl"].ToString();
    var db = context.RequestServices.GetRequiredService<IDbConnection>();

    var feedId = await db.QuerySingleOrDefaultAsync<int?>("SELECT Id FROM Feeds WHERE FeedUrl = @FeedUrl", new { FeedUrl = feedUrl });
    if (feedId == null)
    {
        feedId = await db.ExecuteScalarAsync<int>("INSERT INTO Feeds (FeedUrl) VALUES (@FeedUrl); SELECT last_insert_rowid();", 
                                                  new { FeedUrl = feedUrl });
    }

    var existingAssociation = await db.QuerySingleOrDefaultAsync("SELECT * FROM UserFeeds WHERE UserId = @UserId AND FeedId = @FeedId", 
                                                                 new { UserId = userId, FeedId = feedId });
    if (existingAssociation != null)
    {
        return Results.Ok("<p>This feed is already added.</p>");
    }

    await db.ExecuteAsync("INSERT INTO UserFeeds (UserId, FeedId) VALUES (@UserId, @FeedId)", new { UserId = userId, FeedId = feedId });
    return Results.Ok("<p>Feed added successfully!</p>");
});

// Remove Feed Endpoint
app.MapPost("/removeFeed", async (HttpContext context) =>
{
    if (!context.Request.Cookies.TryGetValue("userId", out string? userIdStr) || !int.TryParse(userIdStr, out int userId))
    {
        return Results.Ok("<p>Please log in to remove feeds.</p>");
    }

    var feedUrl = context.Request.Form["feedUrl"].ToString();
    var db = context.RequestServices.GetRequiredService<IDbConnection>();

    var feedId = await db.QuerySingleOrDefaultAsync<int?>("SELECT Id FROM Feeds WHERE FeedUrl = @FeedUrl", new { FeedUrl = feedUrl });
    if (feedId == null)
    {
        return Results.Ok("<p>Feed not found with the provided URL.</p>");
    }

    var rowsAffected = await db.ExecuteAsync("DELETE FROM UserFeeds WHERE UserId = @UserId AND FeedId = @FeedId", 
                                             new { UserId = userId, FeedId = feedId });
    return rowsAffected > 0 ? Results.Ok("<p>Feed removed successfully!</p>") : Results.Ok("<p>No matching feed found for removal.</p>");
});

// Get User Feeds for Dropdown
app.MapPost("/getUserFeeds", async (HttpContext context) =>
{
    if (!context.Request.Cookies.TryGetValue("userId", out string? userIdStr) || !int.TryParse(userIdStr, out int userId))
    {
        return Results.Ok("<p>Please log in to view feeds.</p>");
    }

    var db = context.RequestServices.GetRequiredService<IDbConnection>();
    var feeds = await db.QueryAsync<string>("SELECT FeedUrl FROM Feeds INNER JOIN UserFeeds ON Feeds.Id = UserFeeds.FeedId WHERE UserFeeds.UserId = @UserId", 
                                            new { UserId = userId });

    var feedOptionsHtml = string.Join("", feeds.Select(feed => $"<option value='{feed}'>{feed}</option>"));
    return Results.Ok(feedOptionsHtml);
});

// Display Feeds Content
app.MapPost("/displayFeeds", async (HttpContext context) =>
{
    if (!context.Request.Cookies.TryGetValue("userId", out string? userIdStr) || !int.TryParse(userIdStr, out int userId))
    {
        return Results.Ok("<p>Please log in to view feed content.</p>");
    }

    var db = context.RequestServices.GetRequiredService<IDbConnection>();
    var feedUrls = await db.QueryAsync<string>("SELECT FeedUrl FROM Feeds INNER JOIN UserFeeds ON Feeds.Id = UserFeeds.FeedId WHERE UserFeeds.UserId = @UserId", 
                                               new { UserId = userId });

    var feedContentHtml = "<div>";
    foreach (var feedUrl in feedUrls)
    {
        try
        {
            using var reader = XmlReader.Create(feedUrl);
            var feed = SyndicationFeed.Load(reader);
            if (feed != null)
            {
                feedContentHtml += $"<h3>{feed.Title.Text}</h3><ul>";
                foreach (var item in feed.Items)
                {
                    feedContentHtml += $"<li><strong>{item.Title.Text}</strong><br>{item.Summary.Text}</li>";
                }
                feedContentHtml += "</ul>";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading feed from URL {feedUrl}: {ex.Message}");
            feedContentHtml += $"<p>Could not load feed from URL: {feedUrl}</p>";
        }
    }
    feedContentHtml += "</div>";

    return Results.Ok(feedContentHtml);
});

app.Run();

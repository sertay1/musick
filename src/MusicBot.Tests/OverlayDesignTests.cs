using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicBot.Controllers;
using MusicBot.Data;
using MusicBot.Tests.TestHelpers;
using Xunit;
namespace MusicBot.Tests;
public class OverlayDesignTests
{
    [Theory]
    [InlineData("static")][InlineData("pulse")][InlineData("chroma")][InlineData("breathe")][InlineData("sweep")][InlineData("flicker")]
    public async Task NeonAndShadowPreferencesRoundTripIndependentlyOfThePalette(string mode)
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db = new MusicBotDbContext(new DbContextOptionsBuilder<MusicBotDbContext>().UseSqlite(connection).Options); db.Database.EnsureCreated();
        var controller = new OverlayPreferencesController(db, FakeHub.Instance);
        Assert.IsType<NoContentResult>(await controller.Put(new() { Theme="midnight",Mode=mode,NeonEnabled=true,NeonColor="#ffbbaa",GlowStrength=.8,NeonSpeed=1.5,BackgroundOpacity=.25,ShowShadow=false,ShadowStrength=.3 }));
        var saved=JsonSerializer.Deserialize<OverlayPreferences>(Assert.IsType<ContentResult>(await controller.Get()).Content!,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("midnight",saved.Theme); Assert.Equal(mode,saved.Mode); Assert.True(saved.NeonEnabled); Assert.False(saved.ShowShadow);
        Assert.Equal("#ffbbaa",saved.NeonColor); Assert.Equal(.8,saved.GlowStrength); Assert.Equal(1.5,saved.NeonSpeed); Assert.Equal(.25,saved.BackgroundOpacity); Assert.Equal(.3,saved.ShadowStrength);
    }
    [Fact]
    public void AppearanceRangesAndLegacyDataAreValidated()
    {
        Assert.False(new OverlayPreferences { BackgroundOpacity=-.1 }.IsValid()); Assert.False(new OverlayPreferences { GlowStrength=2 }.IsValid());
        Assert.False(new OverlayPreferences { ShadowStrength=2 }.IsValid()); Assert.False(new OverlayPreferences { NeonSpeed=0 }.IsValid());
        Assert.False(new OverlayPreferences { NeonColor="url(test)" }.IsValid()); Assert.False(new OverlayPreferences { Mode="unknown" }.IsValid());
        var old=JsonSerializer.Deserialize<OverlayPreferences>("{\"theme\":\"colorful\",\"mode\":\"chroma\"}",new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(old.IsValid()); Assert.Null(old.NeonEnabled); Assert.Equal(1,old.BackgroundOpacity);
        Assert.True(new OverlayPreferences { Theme="warm" }.IsValid());
    }
    [Theory]
    [InlineData("studio")][InlineData("vinyl")][InlineData("broadcast")][InlineData("cassette")][InlineData("type")][InlineData("compact")][InlineData("poster")][InlineData("ribbon")][InlineData("classic")]
    public async Task EveryDesignPersistsWithItsCustomisation(string design)
    {
        using var connection=new SqliteConnection("Data Source=:memory:");connection.Open();
        using var db=new MusicBotDbContext(new DbContextOptionsBuilder<MusicBotDbContext>().UseSqlite(connection).Options);db.Database.EnsureCreated();
        var controller=new OverlayPreferencesController(db,FakeHub.Instance);
        Assert.IsType<NoContentResult>(await controller.Put(new(){Design=design,Radius=7,ShowRequester=false,Accent="#abcdef",QueueLimit=12}));
        var json=Assert.IsType<ContentResult>(await controller.Get()).Content!;var saved=JsonSerializer.Deserialize<OverlayPreferences>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(design,saved.Design);Assert.Equal(7,saved.Radius);Assert.False(saved.ShowRequester);Assert.Equal("#abcdef",saved.Accent);Assert.Equal(12,saved.QueueLimit);Assert.True(saved.IsValid());
    }
    [Fact] public void PreviousPreferencesRetainTheirOptionsWithAValidDefaultDesign(){var old=JsonSerializer.Deserialize<OverlayPreferences>("{\"theme\":\"light\",\"radius\":9}",new JsonSerializerOptions(JsonSerializerDefaults.Web))!;Assert.Equal("studio",old.Design);Assert.Equal("light",old.Theme);Assert.Equal(9,old.Radius);Assert.True(old.IsValid());Assert.False(new OverlayPreferences{Design="unknown"}.IsValid());}
}

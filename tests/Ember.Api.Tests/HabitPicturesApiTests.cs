using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Api.Tests;

/// <summary>Habit pictures and their account preferences (habit-visuals-v1.md, API v1.30).</summary>
public class HabitPicturesApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private sealed record PictureView(string? Picture, int? PortionAmount, string? PortionUnit, bool PictureOfferAnswered);

    private static async Task<PictureView> PictureAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var habit = await response.ExpectJson(status);
        static int? Int(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();
        return new PictureView(habit.GetProperty("picture").GetString(), Int(habit.GetProperty("portionAmount")),
            habit.GetProperty("portionUnit").GetString(), habit.GetProperty("pictureOfferAnswered").GetBoolean());
    }

    private static async Task<int> CreateAsync(ApiClient user, object body)
        => (await (await user.Post("/api/habits", body)).ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();

    private static async Task ExpectError(HttpResponseMessage response, string field, string message)
        => await response.ExpectErrors($$"""{"{{field}}":["{{message}}"]}""");

    [DockerFact]
    public async Task Create_gives_a_picture_without_a_portion_its_default_and_a_plain_habit_no_picture()
    {
        var user = await Host.SignedInAsync();

        const HttpStatusCode created = HttpStatusCode.Created;
        (await PictureAsync(await user.Post("/api/habits", new { name = "Water", frequency = "daily", picture = "bottle" }), created))
            .Should().Be(new PictureView("bottle", 250, "ml", false));
        (await PictureAsync(await user.Post("/api/habits", new { name = "Read", frequency = "weekly", picture = "shelf", portionAmount = 30, portionUnit = "pages", pictureOfferAnswered = true }), created))
            .Should().Be(new PictureView("shelf", 30, "pages", true));
        (await PictureAsync(await user.Post("/api/habits", new { name = "Vitamins", frequency = "daily", picture = "pills" }), created))
            .Should().Be(new PictureView("pills", null, null, false));
        (await PictureAsync(await user.Post("/api/habits", new { name = "Stretch", frequency = "daily" }), created))
            .Should().Be(new PictureView(null, null, null, false));

        await ExpectError(await user.Post("/api/habits", new { name = "Walk", frequency = "daily", picture = "cactus" }),
            "picture", "Must be one of bottle, pills, kindling, shelf, trail.");
        await ExpectError(await user.Post("/api/habits", new { name = "Walk", frequency = "daily", picture = "trail", portionAmount = 99, portionUnit = "m" }),
            "portionAmount", "Must be between 100 and 50000.");
        (await (await user.Get("/api/habits")).ExpectJson()).GetArrayLength().Should().Be(4);
    }

    [DockerFact]
    public async Task Patch_keeps_the_portion_for_the_same_picture_resets_it_on_a_switch_and_clearPicture_removes_both()
    {
        var user = await Host.SignedInAsync();
        var url = $"/api/habits/{await CreateAsync(user, new { name = "Water", frequency = "daily", picture = "bottle", portionAmount = 400, portionUnit = "ml" })}";

        (await PictureAsync(await user.Patch(url, new { name = "Water", picture = "bottle" }))).Should().Be(new PictureView("bottle", 400, "ml", false));
        (await PictureAsync(await user.Patch(url, new { portionAmount = 300, portionUnit = "ml" }))).Should().Be(new PictureView("bottle", 300, "ml", false));
        (await PictureAsync(await user.Patch(url, new { name = "Walk" }))).Should().Be(new PictureView("bottle", 300, "ml", false));
        (await PictureAsync(await user.Patch(url, new { picture = "trail" }))).Should().Be(new PictureView("trail", 10, "min", false));
        (await PictureAsync(await user.Patch(url, new { picture = "kindling" }))).Should().Be(new PictureView("kindling", null, null, false));
        await ExpectError(await user.Patch(url, new { portionAmount = 5, portionUnit = "min" }), "portionAmount", "Pills and kindling have no portion.");
        await ExpectError(await user.Patch(url, new { clearPicture = true, picture = "bottle" }), "picture", "Can't be combined with clearPicture.");
        (await PictureAsync(await user.Patch(url, new { picture = (string?)null }))).Should().Be(new PictureView("kindling", null, null, false));

        (await PictureAsync(await user.Patch(url, new { clearPicture = true }))).Should().Be(new PictureView(null, null, null, false));
        (await PictureAsync(await user.Patch(url, new { clearPicture = true }))).Should().Be(new PictureView(null, null, null, false));
        await ExpectError(await user.Patch(url, new { portionAmount = 250, portionUnit = "ml" }), "portionAmount", "Choose a picture first.");
    }

    [DockerFact]
    public async Task A_rejected_picture_patch_writes_nothing_not_even_the_schedule_sent_with_it()
    {
        var user = await Host.SignedInAsync();
        var url = $"/api/habits/{await CreateAsync(user, new { name = "Read", frequency = "daily", picture = "shelf" })}";

        await ExpectError(await user.Patch(url, new { frequency = "weekly", weeklyTarget = 3, name = "Books", picture = "shelf", portionAmount = 20, portionUnit = "m" }),
            "portionUnit", "Must be min or pages for shelf.");
        await ExpectError(await user.Patch(url, new { timesPerDay = 4, portionAmount = 20 }), "portionAmount", "Send portionAmount and portionUnit together.");

        var habit = await (await user.Get(url)).ExpectJson();
        (habit.GetProperty("frequency").GetString(), habit.GetProperty("timesPerDay").GetInt32(), habit.GetProperty("name").GetString())
            .Should().Be(("daily", 1, "Read"));
        (await PictureAsync(await user.Get(url))).Should().Be(new PictureView("shelf", 15, "min", false));
    }

    [DockerFact]
    public async Task A_let_go_habit_never_gets_a_picture_but_records_the_offer_answer_and_clearPicture_changes_nothing()
    {
        var user = await Host.SignedInAsync();
        const string message = "Never set for let-go habits.";

        await ExpectError(await user.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily", picture = "kindling" }), "picture", message);
        var url = $"/api/habits/{await CreateAsync(user, new { name = "Coffee", kind = "letGo", frequency = "daily" })}";
        await ExpectError(await user.Patch(url, new { picture = "bottle" }), "picture", message);
        await ExpectError(await user.Patch(url, new { portionAmount = 250, portionUnit = "ml" }), "portionAmount", message);

        (await PictureAsync(await user.Patch(url, new { clearPicture = true }))).Should().Be(new PictureView(null, null, null, false));
        (await PictureAsync(await user.Patch(url, new { pictureOfferAnswered = true }))).Should().Be(new PictureView(null, null, null, true));
    }

    [DockerFact]
    public async Task The_offer_answer_is_recorded_once_and_never_taken_back()
    {
        var user = await Host.SignedInAsync();
        var url = $"/api/habits/{await CreateAsync(user, new { name = "Walk", frequency = "daily" })}";

        (await PictureAsync(await user.Patch(url, new { pictureOfferAnswered = false }))).PictureOfferAnswered.Should().BeFalse();
        (await PictureAsync(await user.Patch(url, new { picture = "trail", pictureOfferAnswered = true }))).Should().Be(new PictureView("trail", 10, "min", true));
        (await PictureAsync(await user.Patch(url, new { pictureOfferAnswered = false }))).PictureOfferAnswered.Should().BeTrue();
        (await PictureAsync(await user.Patch(url, new { clearPicture = true }))).Should().Be(new PictureView(null, null, null, true));
    }

    [DockerFact]
    public async Task Me_suggests_pictures_in_metric_by_default_and_patches_both_preferences()
    {
        var user = await Host.SignedInAsync();
        static (bool, string?) Prefs(JsonElement me) => (me.GetProperty("suggestPictures").GetBoolean(), me.GetProperty("units").GetString());

        Prefs(await (await user.Get("/api/me")).ExpectJson()).Should().Be((true, "metric"));
        Prefs(await (await user.Patch("/api/me", new { suggestPictures = false, units = "imperial" })).ExpectJson()).Should().Be((false, "imperial"));
        Prefs(await (await user.Patch("/api/me", new { theme = "tidepool" })).ExpectJson()).Should().Be((false, "imperial"));

        await ExpectError(await user.Patch("/api/me", new { suggestPictures = true, units = "Metric" }), "units", "Must be metric or imperial.");
        Prefs(await (await user.Get("/api/me")).ExpectJson()).Should().Be((false, "imperial"));
    }
}

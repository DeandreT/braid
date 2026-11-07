using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed partial class RegressionCaseHttpTests
{
    [PostgresFact]
    public async Task Automation_http_enforces_csrf_project_admin_and_closed_metadata_bodies()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var root = $"/api/v1/organizations/{data.Mapping.OrganizationId:D}/projects/{data.Mapping.ProjectId:D}";
        var token = await http.TokenAsync();
        foreach (var limit in new[] { "0", "-1", "abc", "2147483648", "101", "01", "0002" })
            foreach (var endpoint in new[] { root + "/notifications", root + "/notification-deliveries", root + "/regression-suites/" + Guid.NewGuid() + "/cases",
            root + "/mappings/" + data.Mapping.MappingId + "/regression-suites", $"/api/v1/organizations/{data.Mapping.OrganizationId:D}/notifications" })
            {
                using var invalid = await http.Client.GetAsync(endpoint + "?limit=" + limit);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.True(invalid.Headers.CacheControl!.NoStore);
            }
        async Task<HttpResponseMessage> Put(string path, string body, bool withToken = true, string? charset = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, root + path) { Content = new StringContent(body, Encoding.UTF8) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = charset };
            request.Headers.Add("Origin", http.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (withToken) request.Headers.Add("X-XSRF-TOKEN", token);
            return await http.Client.SendAsync(request);
        }
        const string policy = """{"requirePassingRegression":true,"errorRatePercent":25,"minimumRequests":20,"windowSeconds":600}""";
        using (var missingToken = await Put("/automation", policy, false)) Assert.Equal(HttpStatusCode.Forbidden, missingToken.StatusCode);
        Assert.Equal(0, http.BodyReads);
        using (var notAdmin = await Put("/automation", policy)) Assert.Equal(HttpStatusCode.NotFound, notAdmin.StatusCode);
        await data.RoleAsync("administrator");
        using (var saved = await Put("/automation", policy, charset: "utf-8")) Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var oversized = await Put("/automation", new string(' ', 8193))) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using (var read = await http.Client.GetAsync(root + "/automation"))
        {
            Assert.True((await Json(read)).GetProperty("requirePassingRegression").GetBoolean());
            Assert.True(read.Headers.CacheControl!.NoStore);
        }
        foreach (var body in new[] {
            """{"events":["revisionUploaded"],"inApp":true,"email":false,"webhooks":[],"secret":"private"}""",
            """{"events":["unknown"],"inApp":true,"email":false,"webhooks":[]}""",
            """{"events":["revisionUploaded"],"inApp":true,"email":false,"webhooks":["https://127.0.0.1"]}""",
            """{"events":["revisionUploaded"],"inApp":true,"email":true,"webhooks":[]}""" })
        {
            using var refusal = await Put("/notification-subscription", body);
            Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
            Assert.DoesNotContain("private", await refusal.Content.ReadAsStringAsync());
        }
        using (var subscription = await Put("/notification-subscription", """{"events":["revisionUploaded"],"inApp":true,"email":false,"webhooks":[]}"""))
            Assert.Equal(HttpStatusCode.OK, subscription.StatusCode);
        using (var read = await http.Client.GetAsync(root + "/notification-subscription"))
            Assert.Equal("revisionUploaded", (await Json(read)).GetProperty("events")[0].GetString());
        using (var otherProject = await http.Client.GetAsync(root.Replace(data.Mapping.ProjectId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal) + "/notifications"))
            Assert.Equal(HttpStatusCode.NotFound, otherProject.StatusCode);

        var resource = Guid.NewGuid();
        await data.Case.ExecuteAsync("SELECT weft.notification_emit($1,$2,'revisionUploaded',NULL,NULL,$3,NULL)", data.Mapping.OrganizationId, data.Mapping.ProjectId, resource);
        var global = $"/api/v1/organizations/{data.Mapping.OrganizationId:D}/notifications";
        string notificationId;
        using (var read = await http.Client.GetAsync(global))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var notification = Assert.Single((await Json(read)).GetProperty("notifications").EnumerateArray());
            Assert.Equal(resource, notification.GetProperty("resourceId").GetGuid());
            notificationId = notification.GetProperty("id").GetString()!;
        }
        using (var request = new HttpRequestMessage(HttpMethod.Put, global + "/" + notificationId + "/read") { Content = new ByteArrayContent("{}"u8.ToArray()) })
        {
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("Origin", http.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-XSRF-TOKEN", await http.TokenAsync());
            using var marked = await http.Client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        }
        using (var read = await http.Client.GetAsync(global))
            Assert.True(Assert.Single((await Json(read)).GetProperty("notifications").EnumerateArray()).GetProperty("read").GetBoolean());
    }
}

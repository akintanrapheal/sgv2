using SterlingLams.Web.Services.Marketing;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>The drag-and-drop composer stores a JSON block model; these cover the server renderer that
/// turns it into the email HTML stored in Campaign.BodyHtml — encoding user text, scheme-checking URLs,
/// and emitting images/buttons the blanket SanitizeRich would otherwise strip.</summary>
public class CampaignLayoutRendererTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Blank_or_invalid_layout_renders_empty(string? json)
        => Assert.Equal("", CampaignLayoutRenderer.Render(json));

    [Fact]
    public void Heading_block_is_html_encoded()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"heading\",\"text\":\"Rings <3 & more\"}]");
        Assert.Contains("Rings &lt;3 &amp; more", html);
        Assert.DoesNotContain("<3", html); // the raw '<' must be encoded
    }

    [Fact]
    public void Text_block_turns_newlines_into_breaks()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"text\",\"text\":\"line one\\nline two\"}]");
        Assert.Contains("line one<br />line two", html);
    }

    [Fact]
    public void Image_block_emits_img_with_safe_src()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"image\",\"src\":\"https://cdn.x/p.jpg\",\"alt\":\"Ring\"}]");
        Assert.Contains("<img src=\"https://cdn.x/p.jpg\"", html);
        Assert.Contains("alt=\"Ring\"", html);
    }

    [Fact]
    public void Image_block_with_no_src_renders_nothing()
        => Assert.Equal("", CampaignLayoutRenderer.Render("[{\"type\":\"image\",\"src\":\"\"}]"));

    [Fact]
    public void Button_defaults_label_and_builds_a_table_button()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"button\",\"href\":\"https://sterlinglams.com\"}]");
        Assert.Contains("<table", html);
        Assert.Contains("href=\"https://sterlinglams.com\"", html);
        Assert.Contains("Shop now", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("ftp://x/y")]
    public void Unsafe_urls_are_dropped_to_hash(string bad)
    {
        var html = CampaignLayoutRenderer.Render($"[{{\"type\":\"button\",\"text\":\"Go\",\"href\":\"{bad}\"}}]");
        Assert.Contains("href=\"#\"", html);
        Assert.DoesNotContain(bad, html);
    }

    [Fact]
    public void Spacer_height_is_clamped()
    {
        Assert.Contains("height:160px", CampaignLayoutRenderer.Render("[{\"type\":\"spacer\",\"size\":9999}]"));
        Assert.Contains("height:4px", CampaignLayoutRenderer.Render("[{\"type\":\"spacer\",\"size\":1}]"));
    }

    [Fact]
    public void Divider_emits_a_rule()
        => Assert.Contains("<hr", CampaignLayoutRenderer.Render("[{\"type\":\"divider\"}]"));

    [Fact]
    public void Social_renders_only_the_links_that_are_set()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"social\",\"instagram\":\"https://instagram.com/sg\",\"facebook\":\"\"}]");
        Assert.Contains("Instagram", html);
        Assert.DoesNotContain("Facebook", html);
    }

    [Fact]
    public void Social_with_no_links_renders_nothing()
        => Assert.Equal("", CampaignLayoutRenderer.Render("[{\"type\":\"social\"}]"));

    [Fact]
    public void Html_block_is_sanitised_but_kept()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"html\",\"html\":\"<p>Hi</p><script>evil()</script>\"}]");
        Assert.Contains("<p>Hi</p>", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void Blocks_render_in_order()
    {
        var html = CampaignLayoutRenderer.Render(
            "[{\"type\":\"heading\",\"text\":\"First\"},{\"type\":\"text\",\"text\":\"Second\"}]");
        Assert.True(html.IndexOf("First") < html.IndexOf("Second"));
    }

    [Fact]
    public void Unknown_block_type_is_skipped()
        => Assert.Equal("", CampaignLayoutRenderer.Render("[{\"type\":\"carousel\"}]"));
}

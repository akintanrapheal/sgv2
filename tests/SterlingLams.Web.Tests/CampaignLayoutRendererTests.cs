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

    [Fact]
    public void Columns_block_renders_a_cell_per_column()
    {
        var html = CampaignLayoutRenderer.Render(
            "[{\"type\":\"columns\",\"cols\":[{\"title\":\"Ring\",\"src\":\"https://cdn.x/r.jpg\",\"btnText\":\"Buy\",\"href\":\"https://sterlinglams.com/r\"},{\"title\":\"Necklace\"}]}]");
        Assert.Contains("<table", html);
        Assert.Contains("Ring", html);
        Assert.Contains("Necklace", html);
        Assert.Contains("width=\"50%\"", html);          // two columns split evenly
        Assert.Contains("href=\"https://sterlinglams.com/r\"", html);
    }

    [Fact]
    public void Columns_with_no_columns_renders_nothing()
        => Assert.Equal("", CampaignLayoutRenderer.Render("[{\"type\":\"columns\",\"cols\":[]}]"));

    [Fact]
    public void Default_visibility_emits_no_media_prelude()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"text\",\"text\":\"hi\"}]");
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("sl-hide", html);
    }

    [Fact]
    public void Desktop_only_block_is_wrapped_and_adds_media_prelude()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"text\",\"text\":\"hi\",\"vis\":\"desktop\"}]");
        Assert.Contains("@media only screen and (max-width:480px)", html);
        Assert.Contains("sl-hide-mobile", html);
    }

    [Fact]
    public void Mobile_only_block_starts_hidden_for_desktop_clients()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"text\",\"text\":\"hi\",\"vis\":\"mobile\"}]");
        Assert.Contains("sl-hide-desktop", html);
        Assert.Contains("display:none", html);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("#ff0000\" onmouseover=\"x")]
    public void Invalid_colour_falls_back_to_the_default(string bad)
    {
        var json = $"[{{\"type\":\"heading\",\"text\":\"Hi\",\"color\":\"{bad.Replace("\"", "\\\"")}\"}}]";
        var html = CampaignLayoutRenderer.Render(json);
        Assert.Contains("color:#111111", html);   // default kept
        Assert.DoesNotContain("onmouseover", html); // no style-attribute break-out
    }

    [Fact]
    public void Valid_hex_colour_is_applied()
        => Assert.Contains("color:#ff8800",
            CampaignLayoutRenderer.Render("[{\"type\":\"heading\",\"text\":\"Hi\",\"color\":\"#ff8800\"}]"));

    [Fact]
    public void Unknown_font_falls_back_to_arial()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"text\",\"text\":\"Hi\",\"font\":\"Comic Sans\\\" x\"}]");
        Assert.Contains("font-family:Arial, Helvetica, sans-serif", html);
    }

    [Fact]
    public void Full_width_button_stretches_the_cell()
    {
        var html = CampaignLayoutRenderer.Render("[{\"type\":\"button\",\"text\":\"Go\",\"href\":\"https://x.io\",\"full\":true}]");
        Assert.Contains("width:100%", html);
        Assert.Contains("display:block", html);
    }
}

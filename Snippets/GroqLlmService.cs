using ElHakim.Application.Common.Interfaces;
using ElHakim.Application.Common.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElHakim.Infrastructure.Services;

/// <summary>
/// LLM service using Groq API (llama-3.3-70b-versatile).
/// Fetches the master CV from Reactive Resume, then generates a tailored structure.
/// </summary>
public class GroqLlmService(
    HttpClient httpClient,
    IReactiveResumeService reactiveResumeService,
    IConfiguration configuration,
    IResumeSchemaValidator schemaValidator,
    ILogger<GroqLlmService> logger) : ILlmService
{
    private const string GroqApiUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string Model = "llama-3.3-70b-versatile";

    private string ApiKey => configuration["GROQ_API_KEY"]
        ?? throw new InvalidOperationException("GROQ_API_KEY is not configured.");

    public async Task<LlmAutoApplyResult> GenerateAutoApplyContentAsync(
        string jobTitle,
        string company,
        string jdText,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Fetching master CV for LLM context...");
        var masterCvJson = await reactiveResumeService.GetMasterCvJsonAsync(cancellationToken);
        
        var (resumeDataStr, context) = ExtractContextAndData(masterCvJson);

        var systemPrompt = BuildSystemPrompt(resumeDataStr);

        // Truncate JD to ~600 words (approx 4000 chars)
        var truncatedJd = jdText.Length > 4000 ? jdText[..4000] : jdText;

        var userMessage = $"""
            Job Title: {jobTitle}
            Company: {company}
            Job Description:
            ---
            {truncatedJd}
            ---
            Generate the tailored MARKDOWN output now.
            """;

        logger.LogInformation("Calling Groq LLM (model: {Model}) for job: {Title} at {Company}", Model, jobTitle, company);

        var requestBody = new
        {
            model = Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userMessage }
            },
            temperature = 0.3,
            max_tokens = 2400
        };

        LlmAutoApplyResult? result = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                result = await CallGroqAsync(requestBody, context, cancellationToken);
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Groq API attempt {Attempt} failed.", attempt);
                if (attempt == 2) throw new InvalidOperationException("Groq API failed after 2 attempts.", ex);
                await Task.Delay(2000, cancellationToken);
            }
        }

        return result!;
    }

    private async Task<LlmAutoApplyResult> CallGroqAsync(object requestBody, ResumeContext context, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GroqApiUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Groq API returned {response.StatusCode}: {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var groqResponse = JsonDocument.Parse(json);
        var content = groqResponse.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? throw new InvalidOperationException("Empty response from Groq.");

        var plan = MarkdownToTailoringPlanParser.Parse(content, context);

        // Validate and sanitize the plan
        plan = schemaValidator.ValidateAndSanitize(plan, context);

        // Compute ATS metrics against TAILORED text - simplistic estimation
        var matched = plan.MatchedKeywords?.Count ?? 0;
        var missing = plan.MissingKeywords?.Count ?? 0;
        var atsScore = Math.Min(100, (int)((double)matched / Math.Max(1, matched + missing) * 100));
        var atsFeedback = missing > 0
            ? $"Missing keywords: {string.Join(", ", plan.MissingKeywords ?? [])}."
            : "Strong match across all detected keywords.";

        // Build patches deterministically
        var ops = BuildPatches(plan, context);

        return new LlmAutoApplyResult(
            ops,
            plan.CoverEmail ?? "",
            atsScore,
            atsFeedback,
            plan.MatchedKeywords?.ToArray() ?? Array.Empty<string>(),
            plan.MissingKeywords?.ToArray() ?? Array.Empty<string>()
        );
    }

    private static (string ResumeDataStr, ResumeContext Context) ExtractContextAndData(string masterCvJson)
    {
        var masterRoot = JsonDocument.Parse(masterCvJson).RootElement;
        if (masterRoot.TryGetProperty("data", out var data))
        {
            masterRoot = data;
        }

        var context = new ResumeContext();

        if (masterRoot.TryGetProperty("sections", out var sections))
        {
            if (sections.TryGetProperty("experience", out var exp) && exp.TryGetProperty("items", out var expItems))
            {
                foreach (var item in expItems.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id)) context.ExperienceIds.Add(id.GetString()!);
                }
            }
            if (sections.TryGetProperty("projects", out var proj) && proj.TryGetProperty("items", out var projItems))
            {
                foreach (var item in projItems.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id)) context.StandardProjectIds.Add(id.GetString()!);
                }
            }
            if (sections.TryGetProperty("skills", out var skills) && skills.TryGetProperty("items", out var skillItems))
            {
                foreach (var item in skillItems.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id)) context.SkillIds.Add(id.GetString()!);
                }
            }
        }

        if (masterRoot.TryGetProperty("customSections", out var customSections))
        {
            foreach (var section in customSections.EnumerateArray())
            {
                if (section.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var id)) context.CustomProjectIds.Add(id.GetString()!);
                    }
                }
            }
        }

        var markdown = ResumeMarkdownSerializer.SerializeToMarkdown(masterRoot);
        return (markdown, context);
    }

    private static string BuildSystemPrompt(string resumeData)
    {
        return $$"""
            You are an expert ATS resume tailoring engine for Abdullah Hakim Mousa.

            YOUR TASK:
            Given a job description, return a TAILORED MARKDOWN document representing the resume.
            DO NOT output JSON. Return ONLY valid Markdown following this EXACT structure:

            # Cover Email
            Plain text email (max 180 words) to the hiring manager.
            
            # Headline
            Targeted headline (max 80 chars)
            
            # Matched Keywords
            - Keyword1
            - Keyword2
            
            # Missing Keywords
            - Keyword3
            
            # Summary
            Tailored summary HTML (only <p> and <br> tags).
            
            (Then include the Experience, Projects, and Skills sections EXACTLY as provided, but with rewritten descriptions/keywords)
            
            MASTER CV MARKDOWN:
            {{resumeData}}

            RULES (follow ALL, in order):
            1. NEVER invent skills, companies, or experiences not in the master CV.
            2. Mirror JD vocabulary EXACTLY.
            3. Experience/Project bullets: [Strong Action Verb] + [Technology from JD] + [Quantified Outcome]. ONLY use <p> and <br> tags.
            4. Skills: Limit to 12 keywords per category. Reorder keywords, putting JD-matched ones first. Format as comma-separated list.
            5. Visibility: Hide projects/skills that don't fit by OMITTING their entire block (including the ID comment) from your output.
            6. YOU MUST PRESERVE the `<!-- ID: uuid -->` comments exactly as they appear in the Master CV for any section you include.
            """;
    }

    private static object[] BuildPatches(TailoringPlan plan, ResumeContext context)
    {
        var ops = new List<object>();

        // 1. /basics/headline
        if (!string.IsNullOrWhiteSpace(plan.Headline))
        {
            ops.Add(new { op = "replace", path = "/basics/headline", value = plan.Headline });
        }

        // 2. /summary/content
        if (!string.IsNullOrWhiteSpace(plan.Summary))
        {
            ops.Add(new { op = "replace", path = "/summary/content", value = plan.Summary });
        }

        // 3. Experience rewrites
        if (plan.ExperienceRewrites != null)
        {
            foreach (var rewrite in plan.ExperienceRewrites)
            {
                var idx = context.ExperienceIds.IndexOf(rewrite.Id);
                if (idx >= 0 && !string.IsNullOrWhiteSpace(rewrite.Description))
                {
                    ops.Add(new { op = "replace", path = $"/sections/experience/items/{idx}/description", value = rewrite.Description });
                }
            }
        }

        // 4. Project rewrites (Standard Projects)
        if (plan.ProjectRewrites != null)
        {
            foreach (var rewrite in plan.ProjectRewrites)
            {
                var stdIdx = context.StandardProjectIds.IndexOf(rewrite.Id);
                if (stdIdx >= 0 && !string.IsNullOrWhiteSpace(rewrite.Description))
                {
                    ops.Add(new { op = "replace", path = $"/sections/projects/items/{stdIdx}/description", value = rewrite.Description });
                }
                
                // Custom Projects are at /customSections/0/items/... (assuming 0 is the index of projects custom section)
                var custIdx = context.CustomProjectIds.IndexOf(rewrite.Id);
                if (custIdx >= 0 && !string.IsNullOrWhiteSpace(rewrite.Description))
                {
                    ops.Add(new { op = "replace", path = $"/customSections/0/items/{custIdx}/description", value = rewrite.Description });
                }
            }
        }

        // 5. Hidden Project Ids
        if (plan.HiddenProjectIds != null)
        {
            foreach (var pId in plan.HiddenProjectIds)
            {
                var stdIdx = context.StandardProjectIds.IndexOf(pId);
                if (stdIdx >= 0)
                    ops.Add(new { op = "replace", path = $"/sections/projects/items/{stdIdx}/hidden", value = true });

                var custIdx = context.CustomProjectIds.IndexOf(pId);
                if (custIdx >= 0)
                    ops.Add(new { op = "replace", path = $"/customSections/0/items/{custIdx}/hidden", value = true });
            }
        }

        // 6. Skill keywords updates
        if (plan.SkillUpdates != null)
        {
            foreach (var skill in plan.SkillUpdates)
            {
                var idx = context.SkillIds.IndexOf(skill.Id);
                if (idx >= 0 && skill.Keywords != null)
                {
                    ops.Add(new { op = "replace", path = $"/sections/skills/items/{idx}/keywords", value = skill.Keywords });
                }
            }
        }

        // 7. Hidden Skill Ids
        if (plan.HiddenSkillIds != null)
        {
            foreach (var sId in plan.HiddenSkillIds)
            {
                var idx = context.SkillIds.IndexOf(sId);
                if (idx >= 0)
                    ops.Add(new { op = "replace", path = $"/sections/skills/items/{idx}/hidden", value = true });
            }
        }

        return ops.ToArray();
    }
}

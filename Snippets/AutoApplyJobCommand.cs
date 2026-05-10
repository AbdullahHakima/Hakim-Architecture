using ElHakim.Application.Common.Interfaces;
using ElHakim.Application.Common.Models;
using ElHakim.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ElHakim.Application.Features.JobApplications.Commands;

public record AutoApplyJobCommand(Guid JobId, bool ForceApply = false) : IRequest<AutoApplyJobResult>;

public record AutoApplyJobResult(
    string ResumeId,
    string ResumeUrl,
    int AtsScore,
    string AtsFeedback,
    string[] KeywordsMatched,
    string[] KeywordsMissing,
    string CoverEmail,
    JobApplicationStatus FinalStatus
);

public class AutoApplyJobCommandHandler(
    IAppDbContext context,
    IJinaScraperService jinaScraperService,
    ILlmService llmService,
    IReactiveResumeService reactiveResumeService,
    IEmailService emailService,
    ILogger<AutoApplyJobCommandHandler> logger) : IRequestHandler<AutoApplyJobCommand, AutoApplyJobResult>
{
    private const int LowMatchThreshold = 50;

    public async Task<AutoApplyJobResult> Handle(AutoApplyJobCommand request, CancellationToken cancellationToken)
    {
        // ── Step 1: Load & Validate Job ────────────────────────────────────────
        var job = await context.JobApplications
            .FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job {request.JobId} not found.");

        logger.LogInformation("Starting auto-apply pipeline for job {JobId}: {Title} at {Company} | Type: {Type} | Source: {Source}",
            job.Id, job.Title, job.Company, job.ApplicationType, job.Source);

        // Mark as Tailoring
        job.UpdateStatus(JobApplicationStatus.Tailoring);
        await context.SaveChangesAsync(cancellationToken);

        // ── Step 2: Get JD Text ────────────────────────────────────────────────
        string jdText;

        if (job.Source == ApplicationSource.ManualText && !string.IsNullOrWhiteSpace(job.JdText))
        {
            // ManualText flow: JD was already pasted by user, no scraping needed
            logger.LogInformation("Step 2: Using stored JD text (ManualText source).");
            jdText = job.JdText;
        }
        else
        {
            // Discovered / ManualUrl flow: scrape JD from URL
            if (string.IsNullOrWhiteSpace(job.Url))
                throw new InvalidOperationException("Cannot scrape JD: job has no URL.");

            logger.LogInformation("Step 2: Scraping JD from {Url}", job.Url);
            jdText = await jinaScraperService.ScrapeJobDescriptionAsync(job.Url, cancellationToken);

            if (string.IsNullOrWhiteSpace(jdText))
                throw new InvalidOperationException($"Could not extract job description from URL: {job.Url}");

            // Cache scraped JD text on the entity for future reference
            job.SetJdText(jdText);
        }

        // ── Step 3: LLM — Tailor CV ─────────────────────────────────────────
        logger.LogInformation("Step 3: Calling LLM to tailor resume...");
        var llmResult = await llmService.GenerateAutoApplyContentAsync(
            job.Title, job.Company, jdText, cancellationToken);

        logger.LogInformation("LLM tailoring complete. ATS Score: {Score}%", llmResult.AtsScore);

        // ── Step 4: Check ATS Score ───────────────────────────────────────────
        if (llmResult.AtsScore < LowMatchThreshold && !request.ForceApply)
        {
            logger.LogWarning("ATS score {Score}% is below threshold {Threshold}%. Marking as LowMatch.",
                llmResult.AtsScore, LowMatchThreshold);

            job.SetAtsScore(llmResult.AtsScore, llmResult.AtsFeedback);
            job.UpdateStatus(JobApplicationStatus.LowMatch);
            await context.SaveChangesAsync(cancellationToken);

            return new AutoApplyJobResult(
                ResumeId: "",
                ResumeUrl: "",
                AtsScore: llmResult.AtsScore,
                AtsFeedback: llmResult.AtsFeedback,
                KeywordsMatched: llmResult.KeywordsMatched,
                KeywordsMissing: llmResult.KeywordsMissing,
                CoverEmail: llmResult.CoverEmail,
                FinalStatus: JobApplicationStatus.LowMatch);
        }

        // ── Step 5: Create & Patch Resume on Reactive Resume ──────────────────
        logger.LogInformation("Step 5: Creating tailored resume on Reactive Resume...");
        var safeCompany = SanitizeSlugPart(job.Company);
        var safeTitle   = SanitizeSlugPart(job.Title);
        var resumeTitle = $"{job.Company} — {job.Title}";
        var resumeSlug  = $"{safeCompany}-{safeTitle}";

        var resumeId = await reactiveResumeService.CreateResumeAsync(resumeTitle, resumeSlug, cancellationToken);

        var masterCvJson = await reactiveResumeService.GetMasterCvJsonAsync(cancellationToken);
        using var masterDoc = System.Text.Json.JsonDocument.Parse(masterCvJson);
        var masterRoot = masterDoc.RootElement;
        if (masterRoot.TryGetProperty("data", out var data)) masterRoot = data;

        var baseOperations = new List<object>();
        foreach (var prop in masterRoot.EnumerateObject())
        {
            baseOperations.Add(new
            {
                op = "replace",
                path = $"/{prop.Name}",
                value = System.Text.Json.JsonSerializer.Deserialize<object>(prop.Value.GetRawText())
            });
        }

        await reactiveResumeService.PatchResumeAsync(resumeId, baseOperations.ToArray(), cancellationToken);

        if (llmResult.PatchOperations.Any())
        {
            await reactiveResumeService.PatchResumeAsync(resumeId, llmResult.PatchOperations, cancellationToken);
        }

        var resumeUrl = $"https://rxresu.me/abdullahhakima/{resumeSlug}";
        job.SetResumeId(resumeId);
        job.SetTailoredResumeUrl(resumeUrl);
        job.SetAtsScore(llmResult.AtsScore, llmResult.AtsFeedback);

        // ── Step 6: Flow-specific action ──────────────────────────────────────
        JobApplicationStatus finalStatus;

        if (job.ApplicationType == ApplicationType.DirectEmail)
        {
            if (string.IsNullOrWhiteSpace(job.RecruiterEmail))
            {
                logger.LogWarning("DirectEmail flow requested but recruiter email is missing. Falling back to ManualApply.");
                job.SetApplicationType(ApplicationType.ManualApply);
                finalStatus = JobApplicationStatus.ReadyToApply;
            }
            else
            {
                logger.LogInformation("Step 6 [DirectEmail]: Queuing job {JobId} for background PDF generation and dispatch.", job.Id);
                
                job.SetCoverLetterText(llmResult.CoverEmail);
                finalStatus = JobApplicationStatus.PendingDispatch;
            }
        }
        else
        {
            // EasyApply or ManualApply: CV is ready, user applies manually from frontend
            logger.LogInformation("Step 6 [{Type}]: CV ready at {Url}. Marking as ReadyToApply.",
                job.ApplicationType, resumeUrl);
            finalStatus = JobApplicationStatus.ReadyToApply;
        }

        // ── Step 7: Save Final Status ──────────────────────────────────────────
        job.UpdateStatus(finalStatus);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Pipeline completed for job {JobId}. Status: {Status} | ATS: {Score}%",
            job.Id, finalStatus, llmResult.AtsScore);

        return new AutoApplyJobResult(
            resumeId,
            resumeUrl,
            llmResult.AtsScore,
            llmResult.AtsFeedback,
            llmResult.KeywordsMatched,
            llmResult.KeywordsMissing,
            llmResult.CoverEmail,
            finalStatus);
    }

    private static string SanitizeSlugPart(string input)
    {
        var sanitized = new string(input.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');
        return sanitized[..Math.Min(24, sanitized.Length)];
    }

    private static string SanitizeFileName(string input)
        => new string(input.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
            .Replace(' ', '_');
}

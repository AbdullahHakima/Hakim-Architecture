using ElHakim.Application.Features.JobApplications.Commands;
using ElHakim.Application.Features.JobApplications.DTOs;
using ElHakim.Application.Features.JobApplications.Queries;
using ElHakim.Application.Common.Interfaces;
using ElHakim.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ElHakim.Api.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobApplicationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public JobApplicationsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<ActionResult<List<JobApplicationDto>>> GetAll()
    {
        var result = await _mediator.Send(new GetJobApplicationsQuery());
        return Ok(result);
    }

    /// <summary>
    /// Creates a job from the JobSpy discovery flow.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Guid>> Create([FromBody] CreateJobApplicationCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    /// <summary>
    /// Creates a job manually from any source (Facebook, Telegram, X, LinkedIn post, WhatsApp, etc.).
    /// Tailoring is triggered separately on-demand via POST /{id}/auto-apply.
    /// </summary>
    [HttpPost("manual")]
    public async Task<ActionResult<Guid>> CreateManual([FromBody] CreateManualJobCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    [HttpGet("discover")]
    public async Task<ActionResult<List<ScrapedJobDto>>> Discover(
        [FromQuery] string keywords = "software engineer",
        [FromQuery] string location = "remote",
        [FromQuery] int count = 10,
        [FromQuery] string sites = "linkedin,indeed",
        [FromQuery] string country = "worldwide")
    {
        var result = await _mediator.Send(new DiscoverJobsQuery(keywords, location, count, sites, country));
        return Ok(result);
    }

    public record ApplyRequest(string RecipientEmail, string EmailBody);

    [HttpPost("{id}/apply")]
    public async Task<ActionResult> Apply(Guid id, [FromBody] ApplyRequest request)
    {
        await _mediator.Send(new SendJobApplicationEmailCommand(id, request.RecipientEmail, request.EmailBody));
        return Ok();
    }

    /// <summary>
    /// Runs the full AI tailoring pipeline for a job.
    /// - DirectEmail jobs: tailors CV + sends email automatically.
    /// - EasyApply / ManualApply jobs: tailors CV + sets status to ReadyToApply.
    /// - LowMatch (ATS less than 50%): skips email, sets status to LowMatch.
    /// Use forceApply=true to override LowMatch and proceed anyway.
    /// </summary>
    [HttpPost("{id}/auto-apply")]
    public async Task<ActionResult<AutoApplyJobResult>> AutoApply(Guid id, [FromQuery] bool forceApply = false)
    {
        var result = await _mediator.Send(new AutoApplyJobCommand(id, forceApply));
        return Ok(result);
    }

    /// <summary>
    /// Overrides the application type (e.g., from EasyApply to DirectEmail after user adds recruiter email).
    /// </summary>
    public record OverrideTypeRequest(ApplicationType ApplicationType);

    [HttpPatch("{id}/type")]
    public async Task<ActionResult> OverrideType(Guid id, [FromBody] OverrideTypeRequest request,
        [FromServices] IAppDbContext dbContext)
    {
        var job = await dbContext.JobApplications.FindAsync([id], HttpContext.RequestAborted);
        if (job is null) return NotFound();
        job.SetApplicationType(request.ApplicationType);
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok();
    }

    /// <summary>
    /// Downloads the tailored PDF resume for a specific job application.
    /// Used by the frontend for "Open & Apply" or Manual flows.
    /// </summary>
    [HttpGet("{id}/resume/pdf")]
    public async Task<ActionResult> DownloadResumePdf(Guid id, 
        [FromServices] IAppDbContext dbContext,
        [FromServices] IReactiveResumeService reactiveResumeService)
    {
        var job = await dbContext.JobApplications.FindAsync([id], HttpContext.RequestAborted);
        if (job is null) return NotFound("Job not found.");
        if (string.IsNullOrEmpty(job.ResumeId)) return BadRequest("No resume has been tailored for this job yet.");

        try
        {
            var pdfBytes = await reactiveResumeService.DownloadPdfAsync(job.ResumeId, HttpContext.RequestAborted);
            
            var safeCompany = new string(job.Company.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Replace(' ', '_');
            var safeTitle = new string(job.Title.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Replace(' ', '_');
            var fileName = $"Resume_{safeCompany}_{safeTitle}.pdf";
            
            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (System.Exception)
        {
            // If the cloud is down during a manual download, return a clear 503 instead of 500
            return StatusCode(503, "The PDF generation cloud service is currently unavailable. Please try downloading again later.");
        }
    }

    /// <summary>
    /// Accepts master CV JSON in the request body and pushes it to the live master resume on Reactive Resume.
    /// Paste the contents of master-cv.json directly from the frontend.
    /// </summary>
    [HttpPost("sync-master-cv")]
    public async Task<ActionResult> SyncMasterCv(
        [FromBody] System.Text.Json.JsonElement masterCvJson,
        [FromServices] IReactiveResumeService reactiveResumeService)
    {
        var json = masterCvJson.GetRawText();
        await reactiveResumeService.UpdateMasterCvAsync(json, HttpContext.RequestAborted);
        return Ok(new { message = "Master CV synced to Reactive Resume successfully." });
    }

    /// <summary>
    /// Dev/test endpoint: runs only the LLM tailoring step without touching Reactive Resume or sending emails.
    /// </summary>
    public record TestLlmRequest(string JobTitle, string Company, string JdText);

    [HttpPost("test-llm")]
    public async Task<ActionResult<LlmAutoApplyResult>> TestLlm([FromBody] TestLlmRequest request,
        [FromServices] ILlmService llmService)
    {
        var result = await llmService.GenerateAutoApplyContentAsync(request.JobTitle, request.Company, request.JdText);
        return Ok(result);
    }
}

using ElHakim.Application.Common.Interfaces;
using ElHakim.Application.Common.Models;
using ElHakim.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElHakim.Infrastructure.BackgroundServices;

/// <summary>
/// A background worker that safely downloads PDFs and sends emails for applications 
/// that are in the PendingDispatch state. This protects the system from cloud outages
/// by continuously retrying until the PDF generation API recovers.
/// </summary>
public class JobDispatchBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<JobDispatchBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job Dispatch Background Service started. Polling every 5 minutes.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred during job dispatch cycle.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessPendingJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var reactiveResumeService = scope.ServiceProvider.GetRequiredService<IReactiveResumeService>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var pendingJobs = await context.JobApplications
            .Where(j => j.Status == JobApplicationStatus.PendingDispatch && j.ResumeId != null)
            .ToListAsync(cancellationToken);

        if (pendingJobs.Count == 0)
            return;

        logger.LogInformation("Found {Count} jobs pending dispatch. Attempting PDF download...", pendingJobs.Count);

        foreach (var job in pendingJobs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(job.RecruiterEmail))
                {
                    logger.LogWarning("Job {JobId} is PendingDispatch but has no recruiter email. Marking as Failed.", job.Id);
                    job.UpdateStatus(JobApplicationStatus.Failed);
                    continue;
                }

                logger.LogInformation("Attempting to generate PDF for job {JobId}...", job.Id);
                var pdfBytes = await reactiveResumeService.DownloadPdfAsync(job.ResumeId!, cancellationToken);

                logger.LogInformation("PDF generated successfully. Sending email...");
                var fileName = $"Resume_{SanitizeFileName(job.Company)}_{SanitizeFileName(job.Title)}.pdf";
                
                var emailMessage = new EmailMessage(
                    To: job.RecruiterEmail,
                    Subject: $"Application for {job.Title} at {job.Company} — Abdullah Hakim Mousa",
                    Body: job.CoverLetterText ?? "Please find my tailored resume attached.",
                    IsHtml: false,
                    FromEmail: "contact@abdullahhakim.me",
                    FromName: "Abdullah Hakim",
                    Attachments: [new(fileName, pdfBytes, "application/pdf")]
                );

                var messageId = await emailService.SendEmailAsync(emailMessage, cancellationToken);
                if (!string.IsNullOrEmpty(messageId)) 
                    job.SetMessageId(messageId);

                job.UpdateStatus(JobApplicationStatus.Applied);
                logger.LogInformation("Successfully dispatched application for {Title} at {Company}.", job.Title, job.Company);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispatch job {JobId}. Will retry on next cycle.", job.Id);
                // Keep status as PendingDispatch so it gets retried on the next cycle
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static string SanitizeFileName(string input)
        => new string(input.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
            .Replace(' ', '_');
}

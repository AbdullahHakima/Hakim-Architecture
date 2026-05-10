using ElHakim.Domain.Common;
using ElHakim.Domain.Enums;

namespace ElHakim.Domain.Entities;

public class JobApplication : BaseEntity
{
    public string Title { get; private set; } = default!;
    public string Company { get; private set; } = default!;
    public string Location { get; private set; } = default!;

    /// <summary>Job posting URL. Null for ManualText source jobs.</summary>
    public string? Url { get; private set; }

    /// <summary>Raw JD text. Populated directly for ManualText source, or after Jina scraping.</summary>
    public string? JdText { get; private set; }

    public JobApplicationStatus Status { get; private set; }

    /// <summary>Auto-detected from recruiter email presence; overridable by user.</summary>
    public ApplicationType ApplicationType { get; private set; }

    /// <summary>Where this job was found.</summary>
    public ApplicationSource Source { get; private set; }

    /// <summary>The Reactive Resume ID of the tailored CV for this application.</summary>
    public string? ResumeId { get; private set; }

    /// <summary>Public Reactive Resume URL for the tailored CV.</summary>
    public string? TailoredResumeUrl { get; private set; }

    public string? RecruiterEmail { get; private set; }
    public string? MessageId { get; private set; }
    public string? ReplyText { get; private set; }
    public int? AtsScore { get; private set; }
    public string? AtsFeedback { get; private set; }
    public string? CoverLetterText { get; private set; }

    public DateTime? DateApplied { get; private set; }

    // Required by EF Core
    private JobApplication() { }

    public JobApplication(
        string title,
        string company,
        string location,
        ApplicationSource source,
        string? url = null,
        string? jdText = null,
        string? recruiterEmail = null)
    {
        Id = Guid.NewGuid();
        Title = title;
        Company = company;
        Location = location;
        Source = source;
        Url = url;
        JdText = jdText;
        RecruiterEmail = recruiterEmail;

        // Auto-detect ApplicationType: if email provided → DirectEmail, else determine from context
        ApplicationType = !string.IsNullOrWhiteSpace(recruiterEmail)
            ? ApplicationType.DirectEmail
            : ApplicationType.EasyApply;

        Status = JobApplicationStatus.Saved;
        CreatedAt = DateTime.UtcNow;
    }

    public void UpdateStatus(JobApplicationStatus newStatus)
    {
        Status = newStatus;
        UpdatedAt = DateTime.UtcNow;

        if (newStatus == JobApplicationStatus.Applied && !DateApplied.HasValue)
        {
            DateApplied = DateTime.UtcNow;
        }
    }

    public void SetApplicationType(ApplicationType type)
    {
        ApplicationType = type;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetJdText(string jdText)
    {
        JdText = jdText;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetMessageId(string messageId)
    {
        MessageId = messageId;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetResumeId(string resumeId)
    {
        ResumeId = resumeId;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetTailoredResumeUrl(string url)
    {
        TailoredResumeUrl = url;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetAtsScore(int score, string? feedback = null)
    {
        AtsScore = score;
        AtsFeedback = feedback;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetCoverLetterText(string text)
    {
        CoverLetterText = text;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateReply(string replyText, JobApplicationStatus newStatus)
    {
        ReplyText = replyText;
        UpdateStatus(newStatus);
    }
}

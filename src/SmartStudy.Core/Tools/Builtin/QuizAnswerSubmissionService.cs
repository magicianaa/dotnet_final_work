using System.Text;
using System.Text.Json;

namespace SmartStudy.Core.Tools.Builtin;

public sealed class QuizAnswerSubmissionService
{
    private readonly SubmitQuizAnswerTool _submit;

    public QuizAnswerSubmissionService(SubmitQuizAnswerTool submit)
    {
        _submit = submit;
    }

    public async Task<string> SubmitAsync(IReadOnlyList<ParsedQuizAnswer> answers, CancellationToken ct = default)
    {
        if (answers.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("### submit_quiz_answer");

        foreach (var answer in answers)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                quizId = answer.QuizId,
                questionNumber = answer.QuestionNumber,
                answer = answer.Answer,
                topic = answer.Topic
            }));

            sb.AppendLine();
            sb.AppendLine(await _submit.InvokeAsync(doc.RootElement, ct));
        }

        return sb.ToString().TrimEnd();
    }
}

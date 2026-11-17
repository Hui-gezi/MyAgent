using System.Text;
using MyAgent.Core;

namespace MyAgent.Context;

public sealed class SystemPromptAssembler : ISystemPromptAssembler
{
    public const string Identity = "You are an AI agent powered by MyAgent.";
    private const int MaxInstructionsBytes = 32 * 1024;
    private static readonly string[] InstructionFiles = ["AGENTS.md", "CLAUDE.md"];

    public string Assemble(Session session, IReadOnlyList<ITool> tools)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine(Identity);
        prompt.AppendLine();
        prompt.AppendLine("You complete software engineering tasks by reading and modifying files, running commands, and verifying results. Work autonomously until the task is done; ask the user only when genuinely blocked.");

        var sections = tools
            .Where(t => t.PromptSection is not null)
            .OrderBy(t => t.PromptOrder)
            .Select(t => t.PromptSection!)
            .ToList();
        if (sections.Count > 0)
        {
            prompt.AppendLine();
            foreach (var section in sections)
                prompt.AppendLine(section);
        }

        foreach (var file in InstructionFiles)
        {
            var path = Path.Combine(session.Cwd, file);
            if (!File.Exists(path)) continue;
            string content;
            try
            {
                content = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException) { continue; }
            if (content.Length > MaxInstructionsBytes)
                content = content[..MaxInstructionsBytes] + "\n[instructions truncated]";
            prompt.AppendLine();
            prompt.AppendLine($"<agent-instructions source=\"{file}\">");
            prompt.AppendLine(content.TrimEnd());
            prompt.AppendLine("</agent-instructions>");
            break;
        }

        return prompt.ToString().TrimEnd();
    }
}
using System.Text;
using System.Text.RegularExpressions;

namespace Mnemos;

public class ClaudeMessage
{
    public string Role { get; set; } = "assistant";
    public string? Text { get; set; }
    public string? Thinking { get; set; }
    public List<CodeBlock> CodeBlocks { get; set; } = new();
    public bool HasContent => !string.IsNullOrEmpty(Text) || !string.IsNullOrEmpty(Thinking);
}

public class CodeBlock
{
    public string Language { get; set; } = "";
    public string Code { get; set; } = "";
}

public static class BlobParser
{
    public static ClaudeMessage? ExtractMessage(byte[] data)
    {
        // 1. Conversion brutale en string (le binaire deviendra des caractères bizarres)
        string rawText = Encoding.UTF8.GetString(data);

        // 2. Le filtre Sniper : On cherche uniquement les longues séquences de texte humain lisible.
        // On autorise les lettres, chiffres, ponctuation standard, accents, sauts de ligne.
        // On exclut totalement les caractères de contrôle et les symboles étendus bizarres.
        MatchCollection matches = Regex.Matches(rawText, @"[a-zA-Z0-9éèàêîôû' \.,!?;:\n\r\(\)\[\]{}""\-]{30,}");

        var fullText = new StringBuilder();

        foreach (Match match in matches)
        {
            string phrase = match.Value.Trim();

            // 3. Filtre anti-bruit système (les mots-clés de Chromium/Claude qu'on ne veut pas voir)
            if (IsSystemNoise(phrase)) continue;

            fullText.AppendLine(phrase);
            fullText.AppendLine(); // Un petit saut de ligne pour aérer
        }

        string finalCleanText = fullText.ToString().Trim();

        // 4. On crée le message si on a trouvé du texte pertinent
        if (!string.IsNullOrEmpty(finalCleanText))
        {
            var msg = new ClaudeMessage
            {
                Text = finalCleanText
            };
            
            msg.CodeBlocks = ExtractCodeBlocks(msg.Text);
            return msg;
        }

        return null;
    }

    // Filtre pour ignorer les métadonnées internes de Claude
    private static bool IsSystemNoise(string text)
    {
        // Ignore les UUIDs stricts
        if (Regex.IsMatch(text, @"^[a-f0-9\-]{36}$")) return true;
        
        // Ignore les artefacts techniques de l'application
        if (text.Contains("u00") || 
            text.Contains("tipTap") || 
            text.Contains("start_timestamp") || 
            text.Contains("stop_timestamp") ||
            text.Contains("sync_source") ||
            text.Contains("parent_message_uuid"))
        {
            return true;
        }

        return false;
    }

    // ===== EXTRACTION DES BLOCS DE CODE =====
    private static List<CodeBlock> ExtractCodeBlocks(string text)
    {
        var blocks = new List<CodeBlock>();
        
        // On cherche le motif Markdown ```langage ... ```
        int currentIndex = 0;
        while ((currentIndex = text.IndexOf("```", currentIndex)) != -1)
        {
            int endOfLanguageIdx = text.IndexOf('\n', currentIndex);
            if (endOfLanguageIdx == -1) break;

            int endOfCodeIdx = text.IndexOf("```", endOfLanguageIdx);
            if (endOfCodeIdx == -1) break;

            string language = text.Substring(currentIndex + 3, endOfLanguageIdx - (currentIndex + 3)).Trim();
            string code = text.Substring(endOfLanguageIdx + 1, endOfCodeIdx - (endOfLanguageIdx + 1)).Trim();

            blocks.Add(new CodeBlock { Language = language, Code = code });
            
            currentIndex = endOfCodeIdx + 3;
        }

        return blocks;
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mnemos;

public static class LdbReader
{
    public static List<Conversation> ExtractConversations(string levelDbPath)
    {
        var allConversations = new List<Conversation>();
        
        // On cible TOUS les fichiers de la DB (ldb et log)
        var files = Directory.GetFiles(levelDbPath, "*.*")
            .Where(f => f.EndsWith(".ldb") || f.EndsWith(".log")).ToList();

        foreach (var file in files)
        {
            try
            {
                // Copie sécurisée
                string tempFile = Path.GetTempFileName();
                File.Copy(file, tempFile, true);
                byte[] data = File.ReadAllBytes(tempFile);
                File.Delete(tempFile);

                if (data.Length < 10) continue;

                // STRATÉGIE : On cherche le marqueur de début V8 (0xFF 0x11)
                // C'est ce qui indique le début d'un objet sérialisé par Chromium
                for (int i = 0; i < data.Length - 10; i++)
                {
                    if (data[i] == 0xFF && data[i + 1] == 0x11)
                    {
                        // On a peut-être un message Claude ici !
                        // On prend un segment de données à partir de là
                        int remaining = Math.Min(data.Length - i, 500000); // Max 500ko par segment
                        byte[] chunk = new byte[remaining];
                        Array.Copy(data, i, chunk, 0, remaining);

                        try
                        {
                            var deserializer = new V8Deserializer(chunk);
                            object? root = deserializer.Deserialize();

                            // Si le désérialiseur arrive à lire quelque chose
                            var found = ConversationExtractor.Extract(root);
                            if (found != null && found.Count > 0)
                            {
                                allConversations.AddRange(found);
                                // On saute un peu plus loin pour éviter de reparser le même objet
                                i += 100; 
                            }
                        }
                        catch 
                        {
                            // Ce n'était pas un objet V8 valide ou pas une conversation, on continue
                        }
                    }
                }
            }
            catch { /* Fichier verrouillé ou illisible */ }
        }

        // On déduplique par UUID pour ne pas avoir 50 fois le même message trouvé dans les logs
        return allConversations
            .GroupBy(c => c.Uuid)
            .Select(g => g.First())
            .ToList();
    }
}
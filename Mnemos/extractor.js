const fs = require('fs');
const path = require('path');
const v8 = require('v8');

const blobDir = process.argv[2];

function walkSync(currentDirPath, callback) {
    if (!fs.existsSync(currentDirPath)) return;
    fs.readdirSync(currentDirPath).forEach(function (name) {
        const filePath = path.join(currentDirPath, name);
        const stat = fs.statSync(filePath);
        if (stat.isFile()) callback(filePath);
    });
}

async function diagnostic() {
    console.error(`\n[NODE] Lancement de l'autopsie V8...`);

    walkSync(blobDir, function(filePath) {
        const buffer = fs.readFileSync(filePath);
        if (buffer.length < 50) return;

        // On cherche le tout PREMIER objet V8 (le gros magot)
        let v8Start = -1;
        for (let i = 0; i < buffer.length - 1; i++) {
            if (buffer[i] === 0xFF && buffer[i+1] >= 0x0A && buffer[i+1] <= 0x20) {
                v8Start = i;
                break; // On s'arrête au premier !
            }
        }

        if (v8Start !== -1) {
            console.error(`\n==================================================`);
            console.error(`[DIAG] Fichier : ${path.basename(filePath)}`);
            console.error(`[DIAG] Début V8 trouvé à l'offset : ${v8Start}`);

            try {
                const deserializer = new v8.Deserializer(buffer.subarray(v8Start));
                deserializer.readHeader();

                // On essaie d'écraser les deux méthodes au cas où (selon la version de Node)
                deserializer.readHostObject = function() { return "[HOST]"; };
                deserializer._readHostObject = function() { return "[HOST]"; };

                // C'est ICI que ça va crasher
                const val = deserializer.readValue();
                console.error(`[DIAG] MIRACLE : Succès de la lecture !`);
            } catch (e) {
                console.error(`[!] CRASH DU MOTEUR V8 : ${e.message}`);
                // On affiche les 20 octets autour de l'erreur pour voir la balise Chromium
                console.error(`[!] Pile d'erreur : ${e.stack.split('\n')[0]}`);
            }
        }
    });
}

diagnostic();
// One-time seed script — run with: node scripts/seed-news.mjs
// Inserts news items into Firestore (musicbot-896cd).
// Safe to re-run: checks by title before inserting to avoid duplicates.

import { initializeApp } from "firebase/app";
import { getFirestore, collection, addDoc, getDocs, query, where, Timestamp } from "firebase/firestore";

const firebaseConfig = {
  apiKey:            "AIzaSyAcgR6ttt-OKIvE-RenmZnSaROsHmtUxqU",
  authDomain:        "musicbot-896cd.firebaseapp.com",
  projectId:         "musicbot-896cd",
  storageBucket:     "musicbot-896cd.firebasestorage.app",
  messagingSenderId: "520541412478",
  appId:             "1:520541412478:web:362373a0136bf280070c2f",
};

const NEWS = [
  {
    title:   "Login con Google — sin abandonar la app",
    excerpt: "Ahora el inicio de sesión con Google se abre en tu navegador de siempre, donde ya tienes tus cuentas cargadas. Sin formularios, sin contraseñas: un clic y vuelves a MusicBot con tu sesión activa.",
    date:    "2026-04-28",
    tag:     "novedad",
  },
  {
    title:   "Comunidad, Novedades y Soporte integrados",
    excerpt: "MusicBot estrena tres nuevas secciones accesibles desde la barra superior. Consulta el historial de cambios, vota por las funciones que quieres ver pronto y envía tickets de soporte directamente desde la app.",
    date:    "2026-04-20",
    tag:     "novedad",
  },
  {
    title:   "Fallback automático ante bloqueos de Content ID",
    excerpt: "Cuando una canción está bloqueada por derechos de autor o simplemente no está disponible, el bot busca automáticamente una versión alternativa en YouTube y continúa la reproducción sin interrumpir la cola.",
    date:    "2026-03-18",
    tag:     "mejora",
  },
  {
    title:   "Cola de fondo independiente con shuffle y promoción",
    excerpt: "La lista de reproducción de fondo ahora vive separada de la biblioteca. Puedes mezclarla con un clic, arrastrar canciones para reordenarlas y promover cualquier pista directamente a la cola activa.",
    date:    "2026-03-10",
    tag:     "mejora",
  },
  {
    title:   "Pre-descarga silenciosa — reproducción sin pausas",
    excerpt: "MusicBot descarga las siguientes canciones de la cola en segundo plano antes de que sean necesarias. El resultado: transiciones instantáneas entre pistas, sin silencios ni esperas incluso con conexiones lentas.",
    date:    "2026-02-22",
    tag:     "mejora",
  },
  {
    title:   "Progreso de descarga en tiempo real en la cola",
    excerpt: "Cada canción en cola muestra su estado de descarga con una barra de progreso animada. Sabrás exactamente cuándo una pista está lista para reproducirse antes de que le llegue su turno.",
    date:    "2026-02-14",
    tag:     "mejora",
  },
  {
    title:   "Reordenamiento con drag & drop en la cola",
    excerpt: "Reorganiza la cola arrastrando las canciones a la posición que prefieras. Los cambios se sincronizan al instante y el bot ajusta la pre-descarga automáticamente para que la nueva orden no genere esperas.",
    date:    "2026-02-01",
    tag:     "mejora",
  },
];

async function main() {
  const app = initializeApp(firebaseConfig);
  const db  = getFirestore(app);
  const col = collection(db, "news");

  console.log(`Insertando ${NEWS.length} novedades...\n`);

  for (const item of NEWS) {
    // Skip if title already exists
    const existing = await getDocs(query(col, where("title", "==", item.title)));
    if (!existing.empty) {
      console.log(`  ⏭  Ya existe: "${item.title}"`);
      continue;
    }

    await addDoc(col, {
      ...item,
      date: Timestamp.fromDate(new Date(item.date)),
    });
    console.log(`  ✅ Insertado: "${item.title}"`);
  }

  console.log("\nListo.");
  process.exit(0);
}

main().catch(e => { console.error(e); process.exit(1); });

// wwwroot/service-worker.js

self.addEventListener('install', event => {
    // Puoi farci precaching in futuro, per ora è giusto un log
    console.log('[SW] Install');
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    console.log('[SW] Activate');
    event.waitUntil(self.clients.claim());
});

// Gestione push: mostra la notifica anche a pagina chiusa
self.addEventListener('push', event => {
    console.log('[SW] Push event ricevuto', event);

    let data = {};
    if (event.data) {
        try {
            data = event.data.json();
        } catch (e) {
            // Se non è JSON, uso il testo grezzo
            data = { title: event.data.text() };
        }
    }

    const title = data.title || 'NextStake';
    const body = data.body || 'Aggiornamento partita.';
    const icon = data.icon || '/icons/favicon_grinch.svg'; // o l’icona che preferisci
    const url = data.url || '/Events'; // pagina di default se non viene passato altro

    const options = {
        body: body,
        icon: icon,
        badge: icon,
        data: { url: url },
        renotify: true
    };

    event.waitUntil(
        self.registration.showNotification(title, options)
    );
});

// Click sulla notifica: apri/focus sulla pagina indicata
self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = event.notification.data && event.notification.data.url;

    if (!url) return;

    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clientList => {
            for (const client of clientList) {
                if (client.url === url && 'focus' in client) {
                    return client.focus();
                }
            }
            if (self.clients.openWindow) {
                return self.clients.openWindow(url);
            }
        })
    );
});

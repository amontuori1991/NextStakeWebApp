// wwwroot/service-worker.js

// (opzionale) semplice install/activate
self.addEventListener('install', event => {
    // Subito attivo
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(clients.claim());
});

// ===== GESTIONE PUSH =====
self.addEventListener('push', event => {
    console.log('[SW] push ricevuto', event);

    let data = {};
    try {
        if (event.data) {
            data = event.data.json();
        }
    } catch (e) {
        console.error('[SW] errore parse payload push', e);
    }

    const title = data.title || 'NextStake';
    const body = data.body || 'Nuova notifica';
    const url = data.url || '/';

    const options = {
        body: body,
        icon: '/icons/android-chrome-192x192.png',
        badge: '/icons/android-chrome-192x192.png',
        data: {
            url: url
        }
    };

    event.waitUntil(
        self.registration.showNotification(title, options)
    );
});

// ===== CLICK SULLA NOTIFICA =====
self.addEventListener('notificationclick', event => {
    console.log('[SW] notificationclick', event);
    event.notification.close();

    const url = (event.notification.data && event.notification.data.url) || '/';

    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true })
            .then(windowClients => {
                // Se esiste già una scheda aperta, la porta in primo piano
                for (const client of windowClients) {
                    if (client.url.includes(self.location.origin)) {
                        client.focus();
                        client.navigate(url);
                        return;
                    }
                }
                // Altrimenti apre una nuova scheda
                return clients.openWindow(url);
            })
    );
});

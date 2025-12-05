// wwwroot/service-worker.js

// Install: attiviamo subito il SW
self.addEventListener('install', event => {
    self.skipWaiting();
});

// Activate: prendiamo il controllo delle pagine aperte
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
        // icona piccola: usa pure quella che hai già in wwwroot/icons
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
                // Se esiste già una scheda del sito, la riutilizziamo
                for (const client of windowClients) {
                    if (client.url && client.url.startsWith(self.location.origin)) {
                        client.focus();
                        client.navigate(url);
                        return;
                    }
                }
                // Altrimenti apriamo una nuova scheda
                return clients.openWindow(url);
            })
    );
});

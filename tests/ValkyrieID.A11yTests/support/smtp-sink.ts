import { mkdirSync, writeFileSync } from 'node:fs';
import { createServer, type Server } from 'node:net';
import { join } from 'node:path';

/**
 * A minimal SMTP sink: accepts every message and writes it to `directory` as `<time>-<n>.eml`, so the tests can follow
 * the links in password reset and verification emails. Plain SMTP only (the test server uses `Smtp:Security=None`).
 */
export function startSmtpSink(port: number, directory: string): Promise<Server> {
  mkdirSync(directory, { recursive: true });
  let count = 0;
  const server = createServer((socket) => {
    let buffer = '';
    let data: string[] | null = null;
    const reply = (line: string) => socket.write(`${line}\r\n`);
    reply('220 localhost ESMTP test sink');
    socket.setEncoding('utf8');
    socket.on('data', (chunk: string) => {
      buffer += chunk;
      let index: number;
      while ((index = buffer.indexOf('\r\n')) >= 0) {
        const line = buffer.slice(0, index);
        buffer = buffer.slice(index + 2);
        if (data) {
          if (line === '.') {
            writeFileSync(join(directory, `${Date.now()}-${count++}.eml`), data.join('\r\n'));
            data = null;
            reply('250 OK');
          } else {
            data.push(line.startsWith('..') ? line.slice(1) : line);
          }

          continue;
        }

        const command = line.slice(0, 4).toUpperCase();
        if (command === 'EHLO' || command === 'HELO') {
          reply('250 localhost');
        } else if (command === 'DATA') {
          data = [];
          reply('354 End data with <CR><LF>.<CR><LF>');
        } else if (command === 'QUIT') {
          reply('221 Bye');
          socket.end();
        } else {
          reply('250 OK');
        }
      }
    });
    socket.on('error', () => undefined);
  });

  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, 'localhost', () => resolve(server));
  });
}

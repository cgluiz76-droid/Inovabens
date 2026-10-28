import type { Metadata } from 'next';
import type { ReactNode } from 'react';
import './globals.css';
import Providers from './providers';

export const metadata: Metadata = { title: 'Inovabens — Gestão Imobiliária', description: 'Gestão de imóveis, financeiro, CRM e agenda' };
export default function RootLayout({ children }: { children: ReactNode }) {
  return <html lang="pt-BR"><body><Providers>{children}</Providers></body></html>;
}

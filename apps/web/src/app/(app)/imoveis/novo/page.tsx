'use client';
import { useRouter } from 'next/navigation';
import { api } from '@/lib/api';
import { ImovelForm } from '@/components/imovel-form';
import { Titulo } from '@/components/ui';

export default function NovoImovel() {
  const r = useRouter();
  return (
    <>
      <Titulo>Novo imóvel</Titulo>
      <div className="card"><ImovelForm rotuloBotao="Cadastrar imóvel" onSalvar={async (c) => { const i = await api.post<{ id: string }>('/imoveis', c); r.push(`/imoveis/${i.id}`); }} /></div>
    </>
  );
}

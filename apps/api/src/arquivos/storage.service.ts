import { CreateBucketCommand, DeleteObjectCommand, GetObjectCommand, HeadBucketCommand, PutObjectCommand, S3Client } from '@aws-sdk/client-s3';
import { getSignedUrl } from '@aws-sdk/s3-request-presigner';
import { Injectable, Logger, OnModuleInit } from '@nestjs/common';
import { env } from '../config/env';

/** Armazenamento S3-compatível (AWS S3, Cloudflare R2, MinIO). Desabilitado se não houver credenciais. */
@Injectable()
export class StorageService implements OnModuleInit {
  private readonly log = new Logger('Storage');
  private readonly client?: S3Client;
  private readonly bucket = env().S3_BUCKET;

  /** Em desenvolvimento cria o bucket (MinIO local) se ainda não existir; em produção o bucket é provisionado à parte. */
  async onModuleInit() {
    if (!this.client || env().NODE_ENV === 'production') return;
    try { await this.client.send(new HeadBucketCommand({ Bucket: this.bucket })); return; } catch { /* não existe ou sem acesso */ }
    try { await this.client.send(new CreateBucketCommand({ Bucket: this.bucket })); this.log.log(`Bucket "${this.bucket}" criado.`); }
    catch (e) { this.log.warn(`Não foi possível acessar/criar o bucket "${this.bucket}": ${e instanceof Error ? e.message : e}`); }
  }

  constructor() {
    const e = env();
    if (e.S3_ACCESS_KEY && e.S3_SECRET_KEY) {
      this.client = new S3Client({
        region: e.S3_REGION,
        endpoint: e.S3_ENDPOINT || undefined,
        forcePathStyle: e.S3_FORCE_PATH_STYLE,
        // compatibilidade com S3-like (Google Cloud Storage, R2): não envia checksums extras que eles rejeitam
        requestChecksumCalculation: 'WHEN_REQUIRED',
        responseChecksumValidation: 'WHEN_REQUIRED',
        credentials: { accessKeyId: e.S3_ACCESS_KEY, secretAccessKey: e.S3_SECRET_KEY },
      });
    } else {
      this.log.warn('S3 não configurado: upload de fotos/documentos desabilitado.');
    }
  }

  get habilitado() { return !!this.client; }

  async enviar(chave: string, corpo: Buffer, contentType: string) {
    await this.client!.send(new PutObjectCommand({ Bucket: this.bucket, Key: chave, Body: corpo, ContentType: contentType }));
  }
  async remover(chave: string) {
    if (!this.client) return;
    await this.client.send(new DeleteObjectCommand({ Bucket: this.bucket, Key: chave })).catch((e) => this.log.warn(`falha ao remover ${chave}: ${e}`));
  }
  /** URL temporária de leitura (1 h). Nunca expomos o bucket publicamente. */
  async urlAssinada(chave: string | null | undefined, segundos = 3600): Promise<string | null> {
    if (!this.client || !chave) return null;
    return getSignedUrl(this.client, new GetObjectCommand({ Bucket: this.bucket, Key: chave }), { expiresIn: segundos });
  }
  async baixar(chave: string): Promise<Buffer | null> {
    if (!this.client) return null;
    try {
      const r = await this.client.send(new GetObjectCommand({ Bucket: this.bucket, Key: chave }));
      return Buffer.from(await r.Body!.transformToByteArray());
    } catch { return null; }
  }
}

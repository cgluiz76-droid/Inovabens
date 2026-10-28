import 'reflect-metadata';
import { NestFactory } from '@nestjs/core';
import { NestExpressApplication } from '@nestjs/platform-express';
import { DocumentBuilder, SwaggerModule } from '@nestjs/swagger';
import cookieParser from 'cookie-parser';
import helmet from 'helmet';
import { AppModule } from './app.module';
import { ProblemFilter } from './common/problem.filter';
import { env } from './config/env';

export async function criarApp() {
  const cfg = env();
  const app = await NestFactory.create<NestExpressApplication>(AppModule, { bufferLogs: false });
  app.set('trust proxy', env().TRUST_PROXY); // atrás do proxy da nuvem (Render/Railway/Fly)
  app.use(helmet());
  app.use(cookieParser());
  app.enableCors({ origin: cfg.CORS_ORIGIN.split(','), credentials: true });
  app.setGlobalPrefix('api/v1', { exclude: [] });
  app.useGlobalFilters(new ProblemFilter());
  app.enableShutdownHooks();
  const doc = SwaggerModule.createDocument(
    app,
    new DocumentBuilder().setTitle('Inovabens API').setVersion('1.0').addBearerAuth().build(),
  );
  SwaggerModule.setup('api/docs', app, doc);
  return app;
}

async function bootstrap() {
  const app = await criarApp();
  await app.listen(env().PORT, '0.0.0.0');
  console.log(`Inovabens API em http://0.0.0.0:${env().PORT}/api/v1 (docs em /api/docs)`);
}
if (require.main === module) bootstrap();

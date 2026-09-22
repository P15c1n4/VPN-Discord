#!/usr/bin/env python3
"""Relay TCP local para OpenVPN/SSTP, com gatekeeper HMAC opcional."""

import argparse
import asyncio
import hashlib
import hmac
import os
import struct
import sys
from dataclasses import dataclass


MAGIC = b"MYPROXY1"
NONCE_LEN = 32
PIPE_CHUNK = 65536
HANDSHAKE_TIMEOUT = 8.0

MODE_GATEKEEPER = "gatekeeper"
MODE_DIRECT = "direct"
PROTO = {"sstp": 0x01, "openvpn": 0x02}


@dataclass(frozen=True)
class RelayConfig:
    edge_host: str
    edge_port: int
    mode: str
    protocol_name: str
    gatekeeper_key: bytes | None


def build_gatekeeper_reply(key: bytes, nonce: bytes, protocol: int) -> bytes:
    mac = hmac.new(key, nonce + struct.pack("!B", protocol), hashlib.sha256).digest()
    return struct.pack("!B", protocol) + mac


async def authenticate_with_gatekeeper(
    reader: asyncio.StreamReader,
    writer: asyncio.StreamWriter,
    key: bytes,
    protocol: int,
) -> None:
    try:
        hello = await asyncio.wait_for(
            reader.readexactly(len(MAGIC) + NONCE_LEN),
            timeout=HANDSHAKE_TIMEOUT,
        )
        if hello[:len(MAGIC)] != MAGIC:
            raise RuntimeError("cabeçalho MYPROXY1 inválido recebido do servidor")

        nonce = hello[len(MAGIC):]
        writer.write(build_gatekeeper_reply(key, nonce, protocol))
        await writer.drain()

        status = await asyncio.wait_for(reader.readexactly(1), timeout=HANDSHAKE_TIMEOUT)
        if status != b"\x00":
            raise RuntimeError("gatekeeper recusou a chave secreta ou o protocolo")
    except asyncio.TimeoutError as error:
        raise RuntimeError(
            f"timeout de {HANDSHAKE_TIMEOUT:g}s aguardando o handshake MYPROXY1; "
            "confirme se o destino executa vpn_gatekeeper.py ou use --mode direct"
        ) from error


async def pipe(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    while data := await reader.read(PIPE_CHUNK):
        writer.write(data)
        await writer.drain()


async def relay_bidirectionally(
    local_reader: asyncio.StreamReader,
    local_writer: asyncio.StreamWriter,
    edge_reader: asyncio.StreamReader,
    edge_writer: asyncio.StreamWriter,
) -> None:
    tasks = {
        asyncio.create_task(pipe(local_reader, edge_writer)),
        asyncio.create_task(pipe(edge_reader, local_writer)),
    }
    try:
        _, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in pending:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
    finally:
        for writer in (local_writer, edge_writer):
            writer.close()
        await asyncio.gather(
            local_writer.wait_closed(),
            edge_writer.wait_closed(),
            return_exceptions=True,
        )


async def handle_local_connection(
    local_reader: asyncio.StreamReader,
    local_writer: asyncio.StreamWriter,
    config: RelayConfig,
) -> None:
    try:
        edge_reader, edge_writer = await asyncio.open_connection(
            config.edge_host,
            config.edge_port,
        )
    except OSError as error:
        print(
            f"[relay] não foi possível alcançar {config.edge_host}:{config.edge_port}: {error}",
            file=sys.stderr,
        )
        local_writer.close()
        await local_writer.wait_closed()
        return

    if config.mode == MODE_GATEKEEPER:
        try:
            if config.gatekeeper_key is None:
                raise RuntimeError("a chave do gatekeeper não foi configurada")
            await authenticate_with_gatekeeper(
                edge_reader,
                edge_writer,
                config.gatekeeper_key,
                PROTO[config.protocol_name],
            )
        except Exception as error:
            print(f"[relay] falha no handshake: {error}", file=sys.stderr)
            local_writer.close()
            edge_writer.close()
            await asyncio.gather(
                local_writer.wait_closed(),
                edge_writer.wait_closed(),
                return_exceptions=True,
            )
            return

    await relay_bidirectionally(local_reader, local_writer, edge_reader, edge_writer)


async def run_relay(args: argparse.Namespace, gatekeeper_key: bytes | None) -> None:
    config = RelayConfig(
        edge_host=args.edge_host,
        edge_port=args.edge_port,
        mode=args.mode,
        protocol_name=args.proto,
        gatekeeper_key=gatekeeper_key,
    )
    server = await asyncio.start_server(
        lambda reader, writer: handle_local_connection(reader, writer, config),
        "127.0.0.1",
        args.local_port,
    )

    print(
        f"[relay] 127.0.0.1:{args.local_port} -> "
        f"{args.edge_host}:{args.edge_port} ({args.proto}, {args.mode})"
    )
    if args.mode == MODE_DIRECT:
        print("[relay] modo direto: handshake MYPROXY1/HMAC desativado")

    async with server:
        await server.serve_forever()


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--proto", choices=list(PROTO), required=True)
    parser.add_argument("--local-port", type=int, required=True)
    parser.add_argument("--edge-host", default=os.environ.get("EDGE_HOST", ""))
    parser.add_argument(
        "--edge-port",
        type=int,
        default=int(os.environ.get("EDGE_PORT", "443")),
    )
    parser.add_argument(
        "--mode",
        choices=(MODE_GATEKEEPER, MODE_DIRECT),
        default=MODE_GATEKEEPER,
        help="gatekeeper exige MYPROXY1/HMAC; direct apenas encaminha o TCP",
    )
    return parser.parse_args()


def load_gatekeeper_key(mode: str) -> bytes | None:
    if mode == MODE_DIRECT:
        return None

    key_hex = os.environ.get("GATEKEEPER_KEY", "")
    if not key_hex:
        sys.exit("defina GATEKEEPER_KEY (a mesma chave hexadecimal do servidor)")

    try:
        key = bytes.fromhex(key_hex)
    except ValueError as error:
        sys.exit(f"GATEKEEPER_KEY não é hexadecimal válida: {error}")

    if not key:
        sys.exit("GATEKEEPER_KEY não pode ser vazia")
    return key


def main() -> None:
    args = parse_arguments()
    if not args.edge_host:
        sys.exit("informe --edge-host ou defina EDGE_HOST")

    gatekeeper_key = load_gatekeeper_key(args.mode)
    try:
        asyncio.run(run_relay(args, gatekeeper_key))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()

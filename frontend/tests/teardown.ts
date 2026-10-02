export default async function teardown() {
  await fetch('http://127.0.0.1:5008/fixture/stop').catch(() => {})
}
